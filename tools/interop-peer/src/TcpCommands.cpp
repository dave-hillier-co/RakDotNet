#include "TcpCommands.h"

#include "Hex.h"
#include "PluginCommands.h"

#include "HTTPConnection.h"
#include "HTTPConnection2.h"
#include "PacketizedTCP.h"
#include "RakString.h"
#include "TCPInterface.h"

#include <arpa/inet.h>
#include <chrono>
#include <limits>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    namespace
    {
        // Reads the listening socket's port, which RakNet keeps protected and does not report.
        template <class Base>
        class ExposedTcp : public Base
        {
        public:
            unsigned short ListenPort()
            {
                if (this->listenSocket == 0)
                    return 0;
                sockaddr_in address;
                socklen_t length = sizeof(address);
                if (getsockname(this->listenSocket, reinterpret_cast<sockaddr*>(&address), &length) != 0)
                    return 0;
                return ntohs(address.sin_port);
            }
        };

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
                throw PluginCommandError(std::string("missing field '") + field + "'");
            return it->get<T>();
        }

        unsigned short PortField(const json& command, const char* field, const long long* fallback)
        {
            long long value = fallback != nullptr ? Optional<long long>(command, field, *fallback) : Required<long long>(command, field);
            if (value < 0 || value > std::numeric_limits<unsigned short>::max())
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<unsigned short>(value);
        }

        std::vector<unsigned char> HexField(const json& command, const char* field)
        {
            std::vector<unsigned char> bytes;
            if (!TryFromHex(Required<std::string>(command, field), bytes))
                throw PluginCommandError(std::string("field '") + field + "' is not valid hex");
            return bytes;
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                return "";
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        // "a.b.c.d:port"; absent or empty is UNASSIGNED_SYSTEM_ADDRESS.
        RakNet::SystemAddress AddressField(const json& command, const char* field)
        {
            std::string text = Optional<std::string>(command, field, "");
            if (text.empty())
                return RakNet::UNASSIGNED_SYSTEM_ADDRESS;
            std::size_t colon = text.rfind(':');
            if (colon == std::string::npos)
                throw PluginCommandError(std::string("field '") + field + "' is not an address");
            long port = std::strtol(text.c_str() + colon + 1, nullptr, 10);
            RakNet::SystemAddress address;
            if (port < 0 || port > 65535 || !address.FromStringExplicitPort(text.substr(0, colon).c_str(), static_cast<unsigned short>(port)))
                throw PluginCommandError(std::string("field '") + field + "' is not an address");
            return address;
        }

        std::string BytesHex(const RakNet::RakString& text)
        {
            return ToHex(reinterpret_cast<const unsigned char*>(text.C_String()), text.GetLength());
        }
    }

    TcpCommands::TcpCommands()
    {
        poller_ = std::thread([this] { PollLoop(); });
    }

    TcpCommands::~TcpCommands()
    {
        stop_ = true;
        if (poller_.joinable())
            poller_.join();
        if (http2_ != nullptr)
        {
            tcp_->DetachPlugin(http2_);
            delete http2_;
        }
        delete http_;
        // The destructor stops the interface, closing descriptor 0 as Stop does; the process is ending.
        if (packetized_ != nullptr)
            delete packetized_;
        else
            delete tcp_;
    }

    void TcpCommands::PollLoop()
    {
        while (!stop_)
        {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (tcp_ != nullptr)
                    PollOnce();
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    // One frame of a RakNet TCP sample. With HTTPConnection the packets go to ProcessTCPPacket and its Update takes the
    // connection events (Samples/PHPDirectoryServer2/main.cpp:42-77); otherwise new connections, completed attempts,
    // packets, failed attempts, then lost connections, the order Samples/ComprehensivePCGame polls in.
    void TcpCommands::PollOnce()
    {
        RakNet::SystemAddress address;
        if (http_ == nullptr)
        {
            while ((address = packetized_ != nullptr ? packetized_->HasNewIncomingConnection() : tcp_->HasNewIncomingConnection()) != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                Record("newIncomingConnection", AddressText(address));
            while ((address = packetized_ != nullptr ? packetized_->HasCompletedConnectionAttempt() : tcp_->HasCompletedConnectionAttempt()) != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                Record("completedConnectionAttempt", AddressText(address));
        }

        for (RakNet::Packet* packet = tcp_->Receive(); packet != nullptr; packet = tcp_->Receive())
        {
            ordered_json event;
            event["kind"] = "packet";
            event["address"] = AddressText(packet->systemAddress);
            event["data"] = ToHex(packet->data, packet->length);
            events_.push_back(event);
            if (http_ != nullptr)
                http_->ProcessTCPPacket(packet);
            tcp_->DeallocatePacket(packet);
        }

        if (http_ != nullptr)
        {
            http_->Update();
            return;
        }
        while ((address = packetized_ != nullptr ? packetized_->HasFailedConnectionAttempt() : tcp_->HasFailedConnectionAttempt()) != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
            Record("failedConnectionAttempt", AddressText(address));
        while ((address = packetized_ != nullptr ? packetized_->HasLostConnection() : tcp_->HasLostConnection()) != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
            Record("lostConnection", AddressText(address));
    }

    void TcpCommands::Record(const char* kind, const std::string& address)
    {
        ordered_json event;
        event["kind"] = kind;
        event["address"] = address;
        events_.push_back(event);
    }

    RakNet::TCPInterface& TcpCommands::RequireTcp()
    {
        if (tcp_ == nullptr)
            throw PluginCommandError("no TCPInterface; send tcpStart first");
        return *tcp_;
    }

    bool TcpCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd.rfind("tcp", 0) != 0 && cmd.rfind("http", 0) != 0)
            return false;
        std::lock_guard<std::mutex> lock(mutex_);

        if (cmd == "tcpStart")
            Start(command, result);
        else if (cmd == "tcpStop")
            Stop();
        else if (cmd == "tcpConnect")
        {
            std::string host = Required<std::string>(command, "host");
            unsigned short port = PortField(command, "port", nullptr);
            bool block = Optional<bool>(command, "block", false);
            result["address"] = AddressText(RequireTcp().Connect(host.c_str(), port, block));
        }
        else if (cmd == "tcpSend")
        {
            std::vector<unsigned char> data = HexField(command, "data");
            RequireTcp().Send(reinterpret_cast<const char*>(data.data()), static_cast<unsigned int>(data.size()),
                              KnownAddress(command, "address"), Optional<bool>(command, "broadcast", false));
        }
        else if (cmd == "tcpSendList")
        {
            json parts = Required<json>(command, "parts");
            if (!parts.is_array())
                throw PluginCommandError("field 'parts' must be an array of hex strings");
            std::vector<std::vector<unsigned char>> buffers;
            for (const json& part : parts)
            {
                std::vector<unsigned char> bytes;
                if (!part.is_string() || !TryFromHex(part.get<std::string>(), bytes))
                    throw PluginCommandError("field 'parts' must be an array of hex strings");
                buffers.push_back(bytes);
            }
            std::vector<const char*> data;
            std::vector<unsigned int> lengths;
            for (const std::vector<unsigned char>& buffer : buffers)
            {
                data.push_back(reinterpret_cast<const char*>(buffer.data()));
                lengths.push_back(static_cast<unsigned int>(buffer.size()));
            }
            result["result"] = RequireTcp().SendList(data.data(), lengths.data(), static_cast<int>(data.size()),
                                                     KnownAddress(command, "address"), Optional<bool>(command, "broadcast", false));
        }
        else if (cmd == "tcpClose")
        {
            if (packetized_ != nullptr)
                packetized_->CloseConnection(KnownAddress(command, "address"));
            else
                RequireTcp().CloseConnection(KnownAddress(command, "address"));
        }
        else if (cmd == "tcpInfo")
            Info(command, result);
        else if (cmd == "tcpPoll")
        {
            RequireTcp();
            result["events"] = events_;
            events_ = ordered_json::array();
        }
        else
            return DispatchHttp(cmd, command, result);
        return true;
    }

    // An address as the interface reported it, with the slot index RakNet keeps in it. CloseConnection needs that index:
    // for an address without one it frees the slot at index systemIndex, out of range (TCPInterface.cpp:593-605).
    RakNet::SystemAddress TcpCommands::KnownAddress(const json& command, const char* field)
    {
        RakNet::SystemAddress address = AddressField(command, field);
        RakNet::SystemAddress connections[64];
        unsigned short count = 64;
        RequireTcp().GetConnectionList(connections, &count);
        for (unsigned short i = 0; i < count && i < 64; ++i)
        {
            if (connections[i] == address)
                return connections[i];
        }
        return address;
    }

    // TCPInterface::Stop closes the socket of every slot, including slots never used, whose socket is 0
    // (TCPInterface.cpp:298-304), so it closes descriptor 0, the peer's stdin. Commands are read on this thread, so stdin
    // is saved around the call and put back.
    void TcpCommands::Stop()
    {
        RakNet::TCPInterface& tcp = RequireTcp();
        int savedStdin = dup(STDIN_FILENO);
        if (packetized_ != nullptr)
            packetized_->Stop();
        else
            tcp.Stop();
        if (savedStdin >= 0)
        {
            dup2(savedStdin, STDIN_FILENO);
            close(savedStdin);
        }
    }

    void TcpCommands::Start(const json& command, ordered_json& result)
    {
        bool packetized = Optional<bool>(command, "packetized", false);
        const long long zero = 0;
        unsigned short port = PortField(command, "port", &zero);
        unsigned short maxIncoming = PortField(command, "maxIncoming", &zero);
        unsigned short maxConnections = PortField(command, "maxConnections", &zero);
        std::string host = Optional<std::string>(command, "host", "");

        if (tcp_ == nullptr)
        {
            if (packetized)
                tcp_ = packetized_ = new ExposedTcp<RakNet::PacketizedTCP>();
            else
                tcp_ = new ExposedTcp<RakNet::TCPInterface>();
        }
        else if (packetized != (packetized_ != nullptr))
            throw PluginCommandError("the TCP interface was created with another value of 'packetized'");

        result["started"] = tcp_->Start(port, maxIncoming, maxConnections, -99999, AF_INET, host.empty() ? nullptr : host.c_str());
        result["port"] = packetized_ != nullptr
            ? static_cast<ExposedTcp<RakNet::PacketizedTCP>*>(packetized_)->ListenPort()
            : static_cast<ExposedTcp<RakNet::TCPInterface>*>(tcp_)->ListenPort();
    }

    void TcpCommands::Info(const json& command, ordered_json& result)
    {
        RakNet::TCPInterface& tcp = RequireTcp();
        result["started"] = tcp.WasStarted();
        RakNet::SystemAddress addresses[64];
        unsigned short count = 64;
        tcp.GetConnectionList(addresses, &count);
        ordered_json connections = ordered_json::array();
        for (unsigned short i = 0; i < count && i < 64; ++i)
            connections.push_back(AddressText(addresses[i]));
        result["connections"] = connections;
        result["connectionCount"] = tcp.GetConnectionCount();
        RakNet::SystemAddress address = KnownAddress(command, "address");
        if (address != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
            result["outgoingBytes"] = tcp.GetOutgoingDataBufferSize(address);
    }

    bool TcpCommands::DispatchHttp(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "httpFormat")
        {
            std::string method = Required<std::string>(command, "method");
            std::string uri = Required<std::string>(command, "uri");
            std::string extraHeaders = Optional<std::string>(command, "extraHeaders", "");
            std::string contentType = Optional<std::string>(command, "contentType", "");
            std::string body = Optional<std::string>(command, "body", "");
            RakNet::RakString request;
            if (method == "GET")
                request = RakNet::RakString::FormatForGET(uri.c_str(), extraHeaders.c_str());
            else if (method == "POST")
                request = RakNet::RakString::FormatForPOST(uri.c_str(), contentType.c_str(), body.c_str(), extraHeaders.c_str());
            else if (method == "PUT")
                request = RakNet::RakString::FormatForPUT(uri.c_str(), contentType.c_str(), body.c_str(), extraHeaders.c_str());
            else if (method == "DELETE")
                request = RakNet::RakString::FormatForDELETE(uri.c_str(), extraHeaders.c_str());
            else
                throw PluginCommandError("field 'method' must be GET, POST, PUT or DELETE");
            result["request"] = BytesHex(request);
            return true;
        }

        if (cmd == "httpConnectionStart")
        {
            RakNet::TCPInterface& tcp = RequireTcp();
            if (http_ != nullptr)
                throw PluginCommandError("an HTTPConnection is already started");
            const long long defaultPort = 80;
            std::string host = Required<std::string>(command, "host");
            http_ = new RakNet::HTTPConnection();
            http_->Init(&tcp, host.c_str(), PortField(command, "port", &defaultPort));
            return true;
        }
        if (cmd == "httpConnectionGet" || cmd == "httpConnectionPost" || cmd == "httpConnectionPoll")
        {
            if (http_ == nullptr)
                throw PluginCommandError("no HTTPConnection; send httpConnectionStart first");
            if (cmd == "httpConnectionGet")
                http_->Get(Required<std::string>(command, "path").c_str());
            else if (cmd == "httpConnectionPost")
            {
                std::string path = Required<std::string>(command, "path");
                std::string data = Required<std::string>(command, "data");
                std::string contentType = Optional<std::string>(command, "contentType", "application/x-www-form-urlencoded");
                http_->Post(path.c_str(), data.c_str(), contentType.c_str());
            }
            else
            {
                result["busy"] = http_->IsBusy();
                result["state"] = http_->GetState();
                result["serverAddress"] = AddressText(http_->GetServerAddress());
                ordered_json results = ordered_json::array();
                while (http_->HasRead())
                    results.push_back(BytesHex(http_->Read()));
                result["results"] = results;
                ordered_json badResponses = ordered_json::array();
                int code;
                RakNet::RakString data;
                while (http_->HasBadResponse(&code, &data))
                {
                    ordered_json bad;
                    bad["code"] = code;
                    bad["data"] = BytesHex(data);
                    badResponses.push_back(bad);
                }
                result["badResponses"] = badResponses;
            }
            return true;
        }

        if (cmd == "http2Attach")
        {
            RakNet::TCPInterface& tcp = RequireTcp();
            if (http2_ == nullptr)
            {
                http2_ = new RakNet::HTTPConnection2();
                tcp.AttachPlugin(http2_);
            }
            return true;
        }
        if (cmd == "http2Transmit" || cmd == "http2Poll")
        {
            if (http2_ == nullptr)
                throw PluginCommandError("no HTTPConnection2 is attached; send http2Attach first");
            if (cmd == "http2Transmit")
            {
                std::vector<unsigned char> request = HexField(command, "request");
                request.push_back(0);
                std::string host = Required<std::string>(command, "host");
                const long long defaultPort = 80;
                result["result"] = http2_->TransmitRequest(reinterpret_cast<const char*>(request.data()), host.c_str(),
                                                           PortField(command, "port", &defaultPort), false, 4,
                                                           AddressField(command, "useAddress"));
            }
            else
            {
                result["busy"] = http2_->IsBusy();
                ordered_json responses = ordered_json::array();
                RakNet::RakString stringTransmitted, hostTransmitted, responseReceived;
                RakNet::SystemAddress hostReceived;
                int contentOffset;
                while (http2_->GetResponse(stringTransmitted, hostTransmitted, responseReceived, hostReceived, contentOffset))
                {
                    ordered_json response;
                    response["stringTransmitted"] = BytesHex(stringTransmitted);
                    response["hostTransmitted"] = std::string(hostTransmitted.C_String());
                    response["response"] = BytesHex(responseReceived);
                    response["hostReceived"] = AddressText(hostReceived);
                    response["contentOffset"] = contentOffset;
                    responses.push_back(response);
                }
                result["responses"] = responses;
            }
            return true;
        }
        return false;
    }
}
