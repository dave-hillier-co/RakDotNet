#include "NatCommands.h"

#include "NatGateway.h"
#include "PluginCommands.h"

#include "NatPunchthroughClient.h"
#include "NatPunchthroughServer.h"
#include "NatTypeDetectionClient.h"
#include "NatTypeDetectionServer.h"
#include "RakMemoryOverride.h"
#include "RakPeerInterface.h"

#include <arpa/inet.h>

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <vector>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    // NatTypeDetectionServer keeps its sockets protected; the harness needs their addresses to know which share a host.
    class ObservableNatTypeDetectionServer : public RakNet::NatTypeDetectionServer
    {
    public:
        std::vector<RakNet::SystemAddress> SocketAddresses() const
        {
            std::vector<RakNet::SystemAddress> addresses;
            for (RakNet::RakNetSocket2* socket : {s1p2, s2p3, s3p4, s4p5})
            {
                if (socket != nullptr)
                    addresses.push_back(socket->GetBoundAddress());
            }
            return addresses;
        }
    };

    namespace
    {
        // NatPunchthroughDebugInterface_Printf writes to stdout, which carries the protocol; this writes to stderr.
        struct StandardErrorDebug : RakNet::NatPunchthroughDebugInterface
        {
            void OnClientMessage(const char* msg) override
            {
                std::fprintf(stderr, "NatPunchthroughClient: %s\n", msg);
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

        long long Ranged(long long value, const char* field, long long minimum, long long maximum)
        {
            if (value < minimum || value > maximum)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return value;
        }

        unsigned short PortField(const json& command, const char* field)
        {
            return static_cast<unsigned short>(Ranged(Required<long long>(command, field), field, 0, 65535));
        }

        RakNet::SystemAddress AddressFields(const json& command)
        {
            std::string host = Optional<std::string>(command, "host", "127.0.0.1");
            unsigned short port = PortField(command, "port");
            RakNet::SystemAddress address;
            if (!address.FromStringExplicitPort(host.c_str(), port))
                throw PluginCommandError("field 'host' is not an address");
            return address;
        }

        RakNet::RakNetGUID GuidField(const json& command, const char* field)
        {
            const json value = Required<json>(command, field);
            if (value.is_number_unsigned())
                return RakNet::RakNetGUID(value.get<std::uint64_t>());
            if (!value.is_string())
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            const std::string text = value.get<std::string>();
            if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            errno = 0;
            unsigned long long g = std::strtoull(text.c_str(), nullptr, 10);
            if (errno == ERANGE)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return RakNet::RakNetGUID(static_cast<std::uint64_t>(g));
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        template <typename T>
        void SetIfPresent(const json& command, const char* field, T& target, long long minimum, long long maximum)
        {
            auto it = command.find(field);
            if (it != command.end() && !it->is_null())
                target = static_cast<T>(Ranged(it->get<long long>(), field, minimum, maximum));
        }
    }

    NatCommands::NatCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    NatCommands::~NatCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here. The NAT type detection plugins
        // are left to the process exit: CreateNonblockingBoundSocket starts a receive thread per socket and
        // NatTypeDetectionServer::Shutdown deletes three of its sockets without stopping theirs, so freeing it crashes.
        if (punchthroughClient_ != nullptr)
            RakNet::NatPunchthroughClient::DestroyInstance(punchthroughClient_);
        if (punchthroughServer_ != nullptr)
            RakNet::NatPunchthroughServer::DestroyInstance(punchthroughServer_);
    }

    bool NatCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "natGateway")
        {
            unsigned short port = PortField(command, "port");
            if (port == 0)
            {
                ClearNatGateway();
                return true;
            }
            std::string host = Optional<std::string>(command, "host", "127.0.0.1");
            in_addr address;
            if (inet_pton(AF_INET, host.c_str(), &address) != 1)
                throw PluginCommandError("field 'host' is not an IPv4 address");
            SetNatGateway(address.s_addr, htons(port));
        }
        else if (cmd == "natTypeDetectionClientAttach")
        {
            if (typeDetectionClient_ == nullptr)
            {
                typeDetectionClient_ = RakNet::NatTypeDetectionClient::GetInstance();
                peer_->AttachPlugin(typeDetectionClient_);
            }
        }
        else if (cmd == "natTypeDetectionClientDetect")
        {
            RequireTypeDetectionClient().DetectNATType(AddressFields(command));
        }
        else if (cmd == "natTypeDetectionServerAttach")
        {
            if (typeDetectionServer_ == nullptr)
            {
                typeDetectionServer_ = RakNet::OP_NEW<ObservableNatTypeDetectionServer>(_FILE_AND_LINE_);
                peer_->AttachPlugin(typeDetectionServer_);
            }
        }
        else if (cmd == "natTypeDetectionServerStartup")
        {
            ObservableNatTypeDetectionServer& server = RequireTypeDetectionServer();
            std::string ip2 = Optional<std::string>(command, "ip2", "127.0.0.1");
            std::string ip3 = Optional<std::string>(command, "ip3", "127.0.0.1");
            std::string ip4 = Optional<std::string>(command, "ip4", "127.0.0.1");
            server.Startup(ip2.c_str(), ip3.c_str(), ip4.c_str());
            ordered_json addresses = ordered_json::array();
            for (const RakNet::SystemAddress& address : server.SocketAddresses())
                addresses.push_back(AddressText(address));
            result["addresses"] = addresses;
        }
        else if (cmd == "natPunchthroughServerAttach")
        {
            if (punchthroughServer_ == nullptr)
            {
                punchthroughServer_ = RakNet::NatPunchthroughServer::GetInstance();
                peer_->AttachPlugin(punchthroughServer_);
            }
        }
        else if (cmd == "natPunchthroughClientAttach")
        {
            if (punchthroughClient_ == nullptr)
            {
                punchthroughClient_ = RakNet::NatPunchthroughClient::GetInstance();
                if (Optional<bool>(command, "debug", false))
                {
                    debug_ = std::make_unique<StandardErrorDebug>();
                    punchthroughClient_->SetDebugInterface(debug_.get());
                }
                peer_->AttachPlugin(punchthroughClient_);
            }
        }
        else if (cmd == "natPunchthroughClientConfigure")
        {
            Configure(command);
        }
        else if (cmd == "natPunchthroughOpen")
        {
            result["result"] = RequirePunchthroughClient().OpenNAT(GuidField(command, "target"), AddressFields(command));
        }
        else if (cmd == "natPunchthroughFindPortStride")
        {
            RequirePunchthroughClient().FindRouterPortStride(AddressFields(command));
        }
        else
        {
            return false;
        }
        return true;
    }

    void NatCommands::Configure(const json& command)
    {
        RakNet::PunchthroughConfiguration& pc = *RequirePunchthroughClient().GetPunchthroughConfiguration();
        const long long maxInt = std::numeric_limits<int>::max();
        SetIfPresent(command, "timeBetweenPunchAttemptsInternal", pc.TIME_BETWEEN_PUNCH_ATTEMPTS_INTERNAL, 0, maxInt);
        SetIfPresent(command, "timeBetweenPunchAttemptsExternal", pc.TIME_BETWEEN_PUNCH_ATTEMPTS_EXTERNAL, 0, maxInt);
        SetIfPresent(command, "udpSendsPerPortInternal", pc.UDP_SENDS_PER_PORT_INTERNAL, 1, maxInt);
        SetIfPresent(command, "udpSendsPerPortExternal", pc.UDP_SENDS_PER_PORT_EXTERNAL, 1, maxInt);
        SetIfPresent(command, "internalIpWaitAfterAttempts", pc.INTERNAL_IP_WAIT_AFTER_ATTEMPTS, 0, maxInt);
        SetIfPresent(command, "maxPredictivePortRange", pc.MAX_PREDICTIVE_PORT_RANGE, 0, maxInt);
        SetIfPresent(command, "externalIpWaitBetweenPorts", pc.EXTERNAL_IP_WAIT_BETWEEN_PORTS, 0, maxInt);
        SetIfPresent(command, "externalIpWaitAfterFirstTtl", pc.EXTERNAL_IP_WAIT_AFTER_FIRST_TTL, 0, maxInt);
        SetIfPresent(command, "externalIpWaitAfterAllAttempts", pc.EXTERNAL_IP_WAIT_AFTER_ALL_ATTEMPTS, 0, maxInt);
        SetIfPresent(command, "maximumNumberOfInternalIdsToCheck", pc.MAXIMUM_NUMBER_OF_INTERNAL_IDS_TO_CHECK, 0, MAXIMUM_NUMBER_OF_INTERNAL_IDS);
        auto retry = command.find("retryOnFailure");
        if (retry != command.end() && !retry->is_null())
            pc.retryOnFailure = retry->get<bool>();
    }

    RakNet::NatTypeDetectionClient& NatCommands::RequireTypeDetectionClient()
    {
        if (typeDetectionClient_ == nullptr)
            throw PluginCommandError("no NatTypeDetectionClient is attached; send natTypeDetectionClientAttach first");
        return *typeDetectionClient_;
    }

    ObservableNatTypeDetectionServer& NatCommands::RequireTypeDetectionServer()
    {
        if (typeDetectionServer_ == nullptr)
            throw PluginCommandError("no NatTypeDetectionServer is attached; send natTypeDetectionServerAttach first");
        return *typeDetectionServer_;
    }

    RakNet::NatPunchthroughClient& NatCommands::RequirePunchthroughClient()
    {
        if (punchthroughClient_ == nullptr)
            throw PluginCommandError("no NatPunchthroughClient is attached; send natPunchthroughClientAttach first");
        return *punchthroughClient_;
    }
}
