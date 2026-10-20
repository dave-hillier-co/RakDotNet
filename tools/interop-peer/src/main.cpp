// interop-peer: one RakNet RakPeerInterface driven by JSON Lines on stdin and stdout.
// The protocol is specified in docs/interop-protocol.md.

#include "Hex.h"
#include "Names.h"
#include "PacketEvent.h"
#include "KeyGeneration.h"
#include "NatCommands.h"
#include "PluginCommands.h"
#include "RelayCommands.h"
#include "SecurityCommands.h"

#include "GetTime.h"
#include "MessageIdentifiers.h"
#include "RakNetStatistics.h"
#include "RakNetTypes.h"
#include "RakNetVersion.h"
#include "RakPeerInterface.h"

#include <nlohmann/json.hpp>

#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <sys/socket.h>
#include <unistd.h>
#include <vector>

using nlohmann::json;
using nlohmann::ordered_json;

namespace
{
    // RakPeer::SetOfflinePingResponse asserts length < 400 (RakPeer.cpp:2271); the assert is
    // compiled out in Release, so the peer enforces it instead of truncating silently.
    constexpr std::size_t kMaxOfflinePingResponse = 399;

    struct CommandError : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // Writes one JSON object per line. Protocol output goes to a private duplicate of the
    // original stdout; file descriptor 1 is pointed at stderr so any printf inside RakNet can
    // never corrupt the JSON stream.
    class Output
    {
    public:
        Output()
        {
            int protocolFd = dup(STDOUT_FILENO);
            if (protocolFd < 0 || dup2(STDERR_FILENO, STDOUT_FILENO) < 0)
            {
                std::perror("interop-peer: redirecting stdout");
                std::exit(2);
            }
            stream_ = fdopen(protocolFd, "w");
            if (stream_ == nullptr)
            {
                std::perror("interop-peer: fdopen");
                std::exit(2);
            }
        }

        void Write(const ordered_json& value)
        {
            std::string line = value.dump(-1, ' ', false, json::error_handler_t::replace);
            line.push_back('\n');
            std::lock_guard<std::mutex> lock(mutex_);
            std::fwrite(line.data(), 1, line.size(), stream_);
            std::fflush(stream_);
        }

        void Log(const std::string& message)
        {
            ordered_json event;
            event["event"] = "log";
            event["message"] = message;
            Write(event);
        }

    private:
        std::mutex mutex_;
        FILE* stream_ = nullptr;
    };

    std::string AddressToString(const RakNet::SystemAddress& address)
    {
        char buffer[128];
        address.ToString(true, buffer, ':');
        return buffer;
    }

    template <typename T>
    T Optional(const json& command, const char* field, T fallback)
    {
        auto it = command.find(field);
        if (it == command.end() || it->is_null())
            return fallback;
        return it->get<T>();
    }

    template <typename T>
    T Required(const json& command, const char* field)
    {
        auto it = command.find(field);
        if (it == command.end() || it->is_null())
            throw CommandError(std::string("missing field '") + field + "'");
        return it->get<T>();
    }

    template <typename T>
    T InRange(long long value, const char* field)
    {
        if (value < static_cast<long long>(std::numeric_limits<T>::min()) ||
            value > static_cast<long long>(std::numeric_limits<T>::max()))
            throw CommandError(std::string("field '") + field + "' is out of range");
        return static_cast<T>(value);
    }

    std::vector<unsigned char> HexField(const json& command, const char* field, bool required)
    {
        std::string text = required ? Required<std::string>(command, field) : Optional<std::string>(command, field, "");
        std::vector<unsigned char> bytes;
        if (!interop::TryFromHex(text, bytes))
            throw CommandError(std::string("field '") + field + "' is not valid hex");
        return bytes;
    }

    RakNet::RakNetGUID ParseGuid(const json& value, const char* field)
    {
        if (value.is_number_unsigned())
            return RakNet::RakNetGUID(value.get<std::uint64_t>());
        if (!value.is_string())
            throw CommandError(std::string("field '") + field + "' must be a decimal GUID string");
        const std::string text = value.get<std::string>();
        if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
            throw CommandError(std::string("field '") + field + "' must be a decimal GUID string");
        errno = 0;
        unsigned long long g = std::strtoull(text.c_str(), nullptr, 10);
        if (errno == ERANGE)
            throw CommandError(std::string("field '") + field + "' is out of range");
        return RakNet::RakNetGUID(static_cast<std::uint64_t>(g));
    }

    class InteropPeer
    {
    public:
        explicit InteropPeer(Output& output) : output_(output), peer_(RakNet::RakPeerInterface::GetInstance()), plugins_(peer_), nat_(peer_), relay_(peer_) {}

        ~InteropPeer()
        {
            StopReceiving();
            RakNet::RakPeerInterface::DestroyInstance(peer_);
        }

        void StartReceiving()
        {
            receiver_ = std::thread([this] { ReceiveLoop(); });
        }

        void StopReceiving()
        {
            stop_ = true;
            if (receiver_.joinable())
                receiver_.join();
        }

        // Handles one command line. Returns false when the process should exit.
        bool HandleLine(const std::string& line)
        {
            if (line.find_first_not_of(" \t\r") == std::string::npos)
                return true;

            json command;
            try
            {
                command = json::parse(line);
            }
            catch (const json::exception& e)
            {
                output_.Log(std::string("ignored line that is not JSON: ") + e.what());
                return true;
            }

            if (!command.is_object() || !command.contains("seq") || !command["seq"].is_number_integer())
            {
                output_.Log("ignored command without an integer 'seq': " + line);
                return true;
            }

            ordered_json reply;
            reply["seq"] = command["seq"];
            bool exitAfterReply = false;

            // One lock covers the RakPeer call and the reply write, so replies and packet
            // events appear on stdout in the order they happened.
            std::lock_guard<std::mutex> lock(peerMutex_);
            try
            {
                ordered_json result;
                exitAfterReply = Dispatch(command, result);
                reply["ok"] = true;
                for (auto it = result.begin(); it != result.end(); ++it)
                    reply[it.key()] = it.value();
            }
            catch (const CommandError& e)
            {
                reply["ok"] = false;
                reply["error"] = e.what();
            }
            catch (const interop::PluginCommandError& e)
            {
                reply["ok"] = false;
                reply["error"] = e.what();
            }
            catch (const interop::SecurityCommandError& e)
            {
                reply["ok"] = false;
                reply["error"] = e.what();
            }
            catch (const json::exception& e)
            {
                reply["ok"] = false;
                reply["error"] = std::string("bad field: ") + e.what();
            }
            output_.Write(reply);
            return !exitAfterReply;
        }

        void ShutdownForEof()
        {
            StopReceiving();
            std::lock_guard<std::mutex> lock(peerMutex_);
            peer_->Shutdown(100);
        }

    private:
        void ReceiveLoop()
        {
            while (!stop_)
            {
                {
                    std::lock_guard<std::mutex> lock(peerMutex_);
                    for (RakNet::Packet* packet = peer_->Receive(); packet != nullptr; packet = peer_->Receive())
                    {
                        output_.Write(interop::DescribePacket(
                            packet->data,
                            packet->length,
                            packet->guid.g,
                            AddressToString(packet->systemAddress),
                            RakNet::GetTimeMS()));
                        peer_->DeallocatePacket(packet);
                    }
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }

        RakNet::SystemAddress RequireConnectedAddress(const json& command, const char* field)
        {
            RakNet::RakNetGUID guid = ParseGuid(Required<json>(command, field), field);
            RakNet::SystemAddress address = peer_->GetSystemAddressFromGuid(guid);
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                throw CommandError("no connection with guid " + std::to_string(guid.g));
            return address;
        }

        // Returns true when the process should exit after the reply is written.
        bool Dispatch(const json& command, ordered_json& result)
        {
            const std::string cmd = Required<std::string>(command, "cmd");

            if (cmd == "startup")
                Startup(command, result);
            else if (cmd == "connect")
                Connect(command, result);
            else if (cmd == "send")
                Send(command, result);
            else if (cmd == "sendList")
                SendList(command, result);
            else if (cmd == "close")
                Close(command);
            else if (cmd == "cancelConnect")
                CancelConnect(command);
            else if (cmd == "ping")
                Ping(command, result);
            else if (cmd == "pingConnected")
                peer_->Ping(RequireConnectedAddress(command, "target"));
            else if (cmd == "setOfflinePingResponse")
                SetOfflinePingResponse(command);
            else if (cmd == "advertise")
                Advertise(command, result);
            else if (cmd == "setTimeout")
                SetTimeout(command);
            else if (cmd == "setOccasionalPing")
                peer_->SetOccasionalPing(Required<bool>(command, "enabled"));
            else if (cmd == "setSplitMessageProgressInterval")
                peer_->SetSplitMessageProgressInterval(InRange<int>(Required<long long>(command, "interval"), "interval"));
            else if (cmd == "setOutgoingBandwidthLimit")
                peer_->SetPerConnectionOutgoingBandwidthLimit(InRange<unsigned>(Required<long long>(command, "bitsPerSecond"), "bitsPerSecond"));
            else if (cmd == "setMaxIncoming")
                peer_->SetMaximumIncomingConnections(InRange<unsigned short>(Required<long long>(command, "count"), "count"));
            else if (cmd == "ban")
                Ban(command);
            else if (cmd == "connectionState")
                ConnectionState(command, result);
            else if (cmd == "stats")
                Stats(command, result);
            else if (cmd == "info")
                Info(result);
            else if (cmd == "time")
                result["time"] = std::to_string(RakNet::GetTime());
            else if (plugins_.Dispatch(cmd, command, result))
            {
            }
            else if (nat_.Dispatch(cmd, command, result))
            {
            }
            else if (relay_.Dispatch(cmd, command, result))
            {
            }
            else if (cmd == "shutdown")
            {
                Shutdown(command);
                return true;
            }
            else if (interop::DispatchSecurityCommand(peer_, cmd, command, result))
                return false;
            else
                throw CommandError("unknown cmd '" + cmd + "'");
            return false;
        }

        void Startup(const json& command, ordered_json& result)
        {
            unsigned short port = InRange<unsigned short>(Optional<long long>(command, "port", 0), "port");
            std::string host = Optional<std::string>(command, "host", "127.0.0.1");
            unsigned int maxConnections = InRange<unsigned short>(Optional<long long>(command, "maxConnections", 8), "maxConnections");
            unsigned short maxIncoming = InRange<unsigned short>(Optional<long long>(command, "maxIncoming", 8), "maxIncoming");

            RakNet::SocketDescriptor descriptor(port, nullptr);
            if (host.size() >= sizeof(descriptor.hostAddress))
                throw CommandError("field 'host' is too long");
            std::strcpy(descriptor.hostAddress, host.c_str());
            descriptor.socketFamily = AF_INET;

            RakNet::StartupResult started = peer_->Startup(maxConnections, &descriptor, 1);
            if (started != RakNet::RAKNET_STARTED)
                throw CommandError("Startup failed: " + interop::StartupResultName(started));
            peer_->SetMaximumIncomingConnections(maxIncoming);

            result["port"] = peer_->GetMyBoundAddress().GetPort();
            result["guid"] = std::to_string(peer_->GetMyGUID().g);
        }

        void Connect(const json& command, ordered_json& result)
        {
            std::string host = Required<std::string>(command, "host");
            unsigned short port = InRange<unsigned short>(Required<long long>(command, "port"), "port");
            std::vector<unsigned char> password = HexField(command, "password", false);
            unsigned attempts = InRange<unsigned>(Optional<long long>(command, "attempts", 12), "attempts");
            unsigned interval = InRange<unsigned>(Optional<long long>(command, "attemptIntervalMs", 500), "attemptIntervalMs");
            RakNet::TimeMS timeout = InRange<RakNet::TimeMS>(Optional<long long>(command, "timeoutMs", 0), "timeoutMs");
            interop::ConnectKey publicKey(command);

            RakNet::ConnectionAttemptResult attempt = peer_->Connect(
                host.c_str(),
                port,
                password.empty() ? nullptr : reinterpret_cast<const char*>(password.data()),
                static_cast<int>(password.size()),
                publicKey.Get(),
                0,
                attempts,
                interval,
                timeout);
            result["result"] = interop::ConnectionAttemptResultName(attempt);
        }

        struct SendParameters
        {
            PacketReliability reliability = RELIABLE_ORDERED;
            PacketPriority priority = HIGH_PRIORITY;
            char channel = 0;
            bool broadcast = false;
            RakNet::RakNetGUID target = RakNet::UNASSIGNED_RAKNET_GUID;
        };

        // The fields shared by send and sendList.
        static SendParameters ParseSendParameters(const json& command)
        {
            SendParameters parameters;
            std::string reliabilityName = Optional<std::string>(command, "reliability", "RELIABLE_ORDERED");
            if (!interop::TryParseReliability(reliabilityName, parameters.reliability))
                throw CommandError("unknown reliability '" + reliabilityName + "'");

            std::string priorityName = Optional<std::string>(command, "priority", "HIGH_PRIORITY");
            if (!interop::TryParsePriority(priorityName, parameters.priority))
                throw CommandError("unknown priority '" + priorityName + "'");

            parameters.channel = static_cast<char>(InRange<unsigned char>(Optional<long long>(command, "channel", 0), "channel"));
            parameters.broadcast = Optional<bool>(command, "broadcast", false);

            // With broadcast, the target is the system to exclude; it may be omitted.
            auto it = command.find("target");
            if (it != command.end() && !it->is_null())
                parameters.target = ParseGuid(*it, "target");
            else if (!parameters.broadcast)
                throw CommandError("missing field 'target'");
            return parameters;
        }

        void Send(const json& command, ordered_json& result)
        {
            std::vector<unsigned char> data = HexField(command, "data", true);
            if (data.empty())
                throw CommandError("field 'data' must contain at least the message ID byte");

            SendParameters parameters = ParseSendParameters(command);
            uint32_t receipt = peer_->Send(
                reinterpret_cast<const char*>(data.data()),
                static_cast<int>(data.size()),
                parameters.priority,
                parameters.reliability,
                parameters.channel,
                RakNet::AddressOrGUID(parameters.target),
                parameters.broadcast);
            result["receipt"] = receipt;
        }

        // RakPeer::SendList sends the parts concatenated as one message (RakPeer.cpp:1434-1480).
        void SendList(const json& command, ordered_json& result)
        {
            json parts = Required<json>(command, "parts");
            if (!parts.is_array() || parts.empty())
                throw CommandError("field 'parts' must be a non-empty array of hex strings");

            std::vector<std::vector<unsigned char>> buffers;
            for (const json& part : parts)
            {
                if (!part.is_string())
                    throw CommandError("field 'parts' must be a non-empty array of hex strings");
                std::vector<unsigned char> bytes;
                if (!interop::TryFromHex(part.get<std::string>(), bytes) || bytes.empty())
                    throw CommandError("field 'parts' must be a non-empty array of hex strings");
                buffers.push_back(bytes);
            }

            std::vector<const char*> data;
            std::vector<int> lengths;
            for (const std::vector<unsigned char>& buffer : buffers)
            {
                data.push_back(reinterpret_cast<const char*>(buffer.data()));
                lengths.push_back(static_cast<int>(buffer.size()));
            }

            SendParameters parameters = ParseSendParameters(command);
            uint32_t receipt = peer_->SendList(
                data.data(),
                lengths.data(),
                static_cast<int>(data.size()),
                parameters.priority,
                parameters.reliability,
                parameters.channel,
                RakNet::AddressOrGUID(parameters.target),
                parameters.broadcast);
            result["receipt"] = receipt;
        }

        void Close(const json& command)
        {
            RakNet::RakNetGUID target = ParseGuid(Required<json>(command, "target"), "target");
            bool notify = Optional<bool>(command, "notify", true);
            unsigned char channel = InRange<unsigned char>(Optional<long long>(command, "channel", 0), "channel");
            peer_->CloseConnection(RakNet::AddressOrGUID(target), notify, channel);
        }

        // The attempt is dropped silently, without ID_CONNECTION_ATTEMPT_FAILED (RakPeer.cpp:1686-1707).
        void CancelConnect(const json& command)
        {
            std::string host = Required<std::string>(command, "host");
            unsigned short port = InRange<unsigned short>(Required<long long>(command, "port"), "port");
            RakNet::SystemAddress target;
            if (!target.FromStringExplicitPort(host.c_str(), port))
                throw CommandError("field 'host' is not an address");
            peer_->CancelConnectionAttempt(target);
        }

        void Ping(const json& command, ordered_json& result)
        {
            std::string host = Required<std::string>(command, "host");
            unsigned short port = InRange<unsigned short>(Required<long long>(command, "port"), "port");
            bool onlyIfAccepting = Optional<bool>(command, "onlyIfAccepting", false);
            result["sent"] = peer_->Ping(host.c_str(), port, onlyIfAccepting);
        }

        void SetOfflinePingResponse(const json& command)
        {
            std::vector<unsigned char> data = HexField(command, "data", true);
            if (data.size() > kMaxOfflinePingResponse)
                throw CommandError("offline ping response must be shorter than 400 bytes");
            peer_->SetOfflinePingResponse(reinterpret_cast<const char*>(data.data()), static_cast<unsigned int>(data.size()));
        }

        void Advertise(const json& command, ordered_json& result)
        {
            std::string host = Required<std::string>(command, "host");
            unsigned short port = InRange<unsigned short>(Required<long long>(command, "port"), "port");
            std::vector<unsigned char> data = HexField(command, "data", false);
            result["sent"] = peer_->AdvertiseSystem(
                host.c_str(),
                port,
                data.empty() ? nullptr : reinterpret_cast<const char*>(data.data()),
                static_cast<int>(data.size()));
        }

        void SetTimeout(const json& command)
        {
            RakNet::TimeMS ms = InRange<RakNet::TimeMS>(Required<long long>(command, "ms"), "ms");
            RakNet::SystemAddress target = RakNet::UNASSIGNED_SYSTEM_ADDRESS;
            auto it = command.find("target");
            if (it != command.end() && !it->is_null())
                target = RequireConnectedAddress(command, "target");
            // UNASSIGNED_SYSTEM_ADDRESS applies to every system and the default (RakPeer.cpp:2526).
            peer_->SetTimeoutTime(ms, target);
        }

        void Ban(const json& command)
        {
            std::string ip = Required<std::string>(command, "ip");
            RakNet::TimeMS ms = InRange<RakNet::TimeMS>(Optional<long long>(command, "ms", 0), "ms");
            peer_->AddToBanList(ip.c_str(), ms);
        }

        void ConnectionState(const json& command, ordered_json& result)
        {
            RakNet::RakNetGUID target = ParseGuid(Required<json>(command, "target"), "target");
            result["state"] = interop::ConnectionStateName(peer_->GetConnectionState(RakNet::AddressOrGUID(target)));
        }

        void Stats(const json& command, ordered_json& result)
        {
            RakNet::SystemAddress address = RequireConnectedAddress(command, "target");
            RakNet::RakNetStatistics statistics;
            if (peer_->GetStatistics(address, &statistics) == nullptr)
                throw CommandError("no statistics for target");

            ordered_json stats;
            stats["messagesInResendBuffer"] = statistics.messagesInResendBuffer;
            stats["bytesInResendBuffer"] = statistics.bytesInResendBuffer;
            stats["packetlossLastSecond"] = statistics.packetlossLastSecond;
            stats["packetlossTotal"] = statistics.packetlossTotal;
            stats["isLimitedByCongestionControl"] = statistics.isLimitedByCongestionControl;
            stats["bpsLimitByCongestionControl"] = statistics.BPSLimitByCongestionControl;
            stats["isLimitedByOutgoingBandwidthLimit"] = statistics.isLimitedByOutgoingBandwidthLimit;
            stats["bpsLimitByOutgoingBandwidthLimit"] = statistics.BPSLimitByOutgoingBandwidthLimit;

            ordered_json runningTotal = ordered_json::object();
            ordered_json valueOverLastSecond = ordered_json::object();
            for (int metric = 0; metric < RakNet::RNS_PER_SECOND_METRICS_COUNT; ++metric)
            {
                runningTotal[interop::PerSecondMetricName(metric)] = statistics.runningTotal[metric];
                valueOverLastSecond[interop::PerSecondMetricName(metric)] = statistics.valueOverLastSecond[metric];
            }
            stats["runningTotal"] = runningTotal;
            stats["valueOverLastSecond"] = valueOverLastSecond;
            result["stats"] = stats;
        }

        void Info(ordered_json& result)
        {
            result["guid"] = std::to_string(peer_->GetMyGUID().g);
            result["boundAddress"] = AddressToString(peer_->GetMyBoundAddress());

            ordered_json connections = ordered_json::array();
            unsigned short count = peer_->GetMaximumNumberOfPeers();
            std::vector<RakNet::SystemAddress> addresses(count > 0 ? count : 1);
            if (count > 0 && peer_->GetConnectionList(addresses.data(), &count))
            {
                for (unsigned short i = 0; i < count; ++i)
                {
                    ordered_json connection;
                    connection["guid"] = std::to_string(peer_->GetGuidFromSystemAddress(addresses[i]).g);
                    connection["address"] = AddressToString(addresses[i]);
                    connection["mtu"] = peer_->GetMTUSize(addresses[i]);
                    connection["averagePing"] = peer_->GetAveragePing(RakNet::AddressOrGUID(addresses[i]));
                    connection["lastPing"] = peer_->GetLastPing(RakNet::AddressOrGUID(addresses[i]));
                    connection["lowestPing"] = peer_->GetLowestPing(RakNet::AddressOrGUID(addresses[i]));
                    connection["externalAddress"] = AddressToString(peer_->GetExternalID(addresses[i]));
                    connections.push_back(connection);
                }
            }
            result["connections"] = connections;
        }

        void Shutdown(const json& command)
        {
            unsigned int blockMs = InRange<unsigned int>(Optional<long long>(command, "blockMs", 100), "blockMs");
            peer_->Shutdown(blockMs);
        }

        Output& output_;
        RakNet::RakPeerInterface* peer_;
        std::mutex peerMutex_;
        std::thread receiver_;
        std::atomic<bool> stop_{false};
        interop::PluginCommands plugins_;
        interop::NatCommands nat_;
        interop::RelayCommands relay_;
    };
}

int main()
{
    Output output;

    int exitCode = 0;
    {
        InteropPeer peer(output);

        ordered_json ready;
        ready["event"] = "ready";
        ready["raknetVersion"] = RAKNET_VERSION;
        ready["protocolVersion"] = RAKNET_PROTOCOL_VERSION;
        ready["security"] = interop::SecurityCompiledIn();
        output.Write(ready);

        peer.StartReceiving();

        std::string line;
        bool keepRunning = true;
        while (keepRunning && std::getline(std::cin, line))
            keepRunning = peer.HandleLine(line);

        if (keepRunning)
            peer.ShutdownForEof();
    }
    return exitCode;
}
