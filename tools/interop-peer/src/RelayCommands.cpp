#include "RelayCommands.h"

#include "Hex.h"
#include "NatFields.h"
#include "Names.h"
#include "PluginCommands.h"

#include "BitStream.h"
#include "RakPeerInterface.h"
#include "RakString.h"
#include "RelayPlugin.h"
#include "Router2.h"
#include "UDPForwarder.h"
#include "UDPProxyClient.h"
#include "UDPProxyCoordinator.h"
#include "UDPProxyServer.h"

#include <limits>
#include <vector>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    using namespace natfields;

    namespace
    {
        const char* ForwarderResultName(RakNet::UDPForwarderResult result)
        {
            switch (result)
            {
            case RakNet::UDPFORWARDER_FORWARDING_ALREADY_EXISTS: return "UDPFORWARDER_FORWARDING_ALREADY_EXISTS";
            case RakNet::UDPFORWARDER_NO_SOCKETS: return "UDPFORWARDER_NO_SOCKETS";
            case RakNet::UDPFORWARDER_BIND_FAILED: return "UDPFORWARDER_BIND_FAILED";
            case RakNet::UDPFORWARDER_INVALID_PARAMETERS: return "UDPFORWARDER_INVALID_PARAMETERS";
            case RakNet::UDPFORWARDER_NOT_RUNNING: return "UDPFORWARDER_NOT_RUNNING";
            case RakNet::UDPFORWARDER_SUCCESS: return "UDPFORWARDER_SUCCESS";
            default: return "UDPFORWARDER_RESULT_COUNT";
            }
        }

        const char* RelayResultName(RakNet::RelayPluginEnums result)
        {
            switch (result)
            {
            case RakNet::RPE_ADD_CLIENT_NOT_ALLOWED: return "RPE_ADD_CLIENT_NOT_ALLOWED";
            case RakNet::RPE_ADD_CLIENT_TARGET_NOT_CONNECTED: return "RPE_ADD_CLIENT_TARGET_NOT_CONNECTED";
            case RakNet::RPE_ADD_CLIENT_NAME_ALREADY_IN_USE: return "RPE_ADD_CLIENT_NAME_ALREADY_IN_USE";
            case RakNet::RPE_ADD_CLIENT_SUCCESS: return "RPE_ADD_CLIENT_SUCCESS";
            default: return "RPE_UNKNOWN";
            }
        }

        std::string GuidText(const RakNet::RakNetGUID& guid)
        {
            return std::to_string(guid.g);
        }

        // The data and bit length of a relayed message: data (hex) and bits (data's length in bits), the bits taken
        // from the start of data, most significant first.
        void ReadBits(const json& command, RakNet::BitStream& out)
        {
            std::vector<unsigned char> bytes;
            if (!TryFromHex(Optional<std::string>(command, "data", ""), bytes))
                throw PluginCommandError("field 'data' is not valid hex");
            const long long available = static_cast<long long>(bytes.size()) * 8;
            const long long bits = Ranged(Optional<long long>(command, "bits", available), "bits", 0, available);
            if (bits > 0)
                out.WriteBits(bytes.data(), static_cast<RakNet::BitSize_t>(bits), false);
        }

        PacketPriority PriorityField(const json& command)
        {
            PacketPriority priority;
            if (!TryParsePriority(Optional<std::string>(command, "priority", "HIGH_PRIORITY"), priority))
                throw PluginCommandError("field 'priority' is not a priority");
            return priority;
        }

        PacketReliability ReliabilityField(const json& command)
        {
            PacketReliability reliability;
            if (!TryParseReliability(Optional<std::string>(command, "reliability", "RELIABLE_ORDERED"), reliability))
                throw PluginCommandError("field 'reliability' is not a reliability");
            return reliability;
        }
    }

    // UDPProxyClientResultHandler and UDPProxyServerResultHandler, recording each call for udpProxyCallbacks.
    class ProxyResultRecorder : public RakNet::UDPProxyClientResultHandler, public RakNet::UDPProxyServerResultHandler
    {
    public:
        const ordered_json& Calls() const { return calls_; }

        void OnForwardingSuccess(const char* proxyIPAddress, unsigned short proxyPort, RakNet::SystemAddress proxyCoordinator,
                                 RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress, RakNet::RakNetGUID targetGuid,
                                 RakNet::UDPProxyClient*) override
        {
            Forwarding("OnForwardingSuccess", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnForwardingNotification(const char* proxyIPAddress, unsigned short proxyPort, RakNet::SystemAddress proxyCoordinator,
                                      RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress, RakNet::RakNetGUID targetGuid,
                                      RakNet::UDPProxyClient*) override
        {
            Forwarding("OnForwardingNotification", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnNoServersOnline(RakNet::SystemAddress proxyCoordinator, RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress,
                               RakNet::RakNetGUID targetGuid, RakNet::UDPProxyClient*) override
        {
            Failure("OnNoServersOnline", proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnRecipientNotConnected(RakNet::SystemAddress proxyCoordinator, RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress,
                                     RakNet::RakNetGUID targetGuid, RakNet::UDPProxyClient*) override
        {
            Failure("OnRecipientNotConnected", proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnAllServersBusy(RakNet::SystemAddress proxyCoordinator, RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress,
                              RakNet::RakNetGUID targetGuid, RakNet::UDPProxyClient*) override
        {
            Failure("OnAllServersBusy", proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnForwardingInProgress(const char* proxyIPAddress, unsigned short proxyPort, RakNet::SystemAddress proxyCoordinator,
                                    RakNet::SystemAddress sourceAddress, RakNet::SystemAddress targetAddress, RakNet::RakNetGUID targetGuid,
                                    RakNet::UDPProxyClient*) override
        {
            Forwarding("OnForwardingInProgress", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);
        }

        void OnLoginSuccess(RakNet::RakString usedPassword, RakNet::UDPProxyServer*) override { Login("OnLoginSuccess", usedPassword); }

        void OnAlreadyLoggedIn(RakNet::RakString usedPassword, RakNet::UDPProxyServer*) override { Login("OnAlreadyLoggedIn", usedPassword); }

        void OnNoPasswordSet(RakNet::RakString usedPassword, RakNet::UDPProxyServer*) override { Login("OnNoPasswordSet", usedPassword); }

        void OnWrongPassword(RakNet::RakString usedPassword, RakNet::UDPProxyServer*) override { Login("OnWrongPassword", usedPassword); }

    private:
        void Forwarding(const char* name, const char* proxyIPAddress, unsigned short proxyPort, const RakNet::SystemAddress& proxyCoordinator,
                        const RakNet::SystemAddress& sourceAddress, const RakNet::SystemAddress& targetAddress, const RakNet::RakNetGUID& targetGuid)
        {
            ordered_json call;
            call["name"] = name;
            call["proxyIp"] = proxyIPAddress;
            call["proxyPort"] = proxyPort;
            Addresses(call, proxyCoordinator, sourceAddress, targetAddress, targetGuid);
            calls_.push_back(call);
        }

        void Failure(const char* name, const RakNet::SystemAddress& proxyCoordinator, const RakNet::SystemAddress& sourceAddress,
                     const RakNet::SystemAddress& targetAddress, const RakNet::RakNetGUID& targetGuid)
        {
            ordered_json call;
            call["name"] = name;
            Addresses(call, proxyCoordinator, sourceAddress, targetAddress, targetGuid);
            calls_.push_back(call);
        }

        static void Addresses(ordered_json& call, const RakNet::SystemAddress& proxyCoordinator, const RakNet::SystemAddress& sourceAddress,
                              const RakNet::SystemAddress& targetAddress, const RakNet::RakNetGUID& targetGuid)
        {
            call["coordinator"] = AddressText(proxyCoordinator);
            call["source"] = AddressText(sourceAddress);
            call["target"] = AddressText(targetAddress);
            call["targetGuid"] = GuidText(targetGuid);
        }

        void Login(const char* name, const RakNet::RakString& password)
        {
            ordered_json call;
            call["name"] = name;
            call["password"] = password.C_String();
            calls_.push_back(call);
        }

        ordered_json calls_ = ordered_json::array();
    };

    RelayCommands::RelayCommands(RakNet::RakPeerInterface* peer) : peer_(peer), recorder_(std::make_unique<ProxyResultRecorder>()) {}

    RelayCommands::~RelayCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here. Each forwarder stops its thread
        // when it is destroyed.
        delete forwarder_;
        if (proxyClient_ != nullptr)
            RakNet::UDPProxyClient::DestroyInstance(proxyClient_);
        if (proxyServer_ != nullptr)
            RakNet::UDPProxyServer::DestroyInstance(proxyServer_);
        if (proxyCoordinator_ != nullptr)
            RakNet::UDPProxyCoordinator::DestroyInstance(proxyCoordinator_);
        if (router2_ != nullptr)
            RakNet::Router2::DestroyInstance(router2_);
        if (relay_ != nullptr)
            RakNet::RelayPlugin::DestroyInstance(relay_);
    }

    bool RelayCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        return DispatchForwarder(cmd, command, result) || DispatchProxy(cmd, command, result) || DispatchRouter2(cmd, command, result)
               || DispatchRelay(cmd, command, result);
    }

    bool RelayCommands::DispatchForwarder(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "udpForwarderStartup")
        {
            if (forwarder_ == nullptr)
                forwarder_ = new RakNet::UDPForwarder();
            if (Has(command, "maxEntries"))
                forwarder_->SetMaxForwardEntries(static_cast<unsigned short>(Ranged(Required<long long>(command, "maxEntries"), "maxEntries", 1, 65535 / 2 - 1)));
            forwarder_->Startup();
        }
        else if (cmd == "udpForwarderStart")
        {
            RakNet::SystemAddress source = AddressField(command, "source");
            RakNet::SystemAddress destination = AddressField(command, "destination");
            auto timeout = static_cast<RakNet::TimeMS>(Ranged(Required<long long>(command, "timeoutMs"), "timeoutMs", 0, std::numeric_limits<RakNet::TimeMS>::max()));
            std::string forceHost = Optional<std::string>(command, "forceHost", "");
            unsigned short port = 0;
            RakNet::UDPForwarderResult forwarded = RequireForwarder().StartForwarding(
                source, destination, timeout, forceHost.empty() ? nullptr : forceHost.c_str(), AF_INET, &port, nullptr);
            result["result"] = ForwarderResultName(forwarded);
            result["port"] = port;
        }
        else if (cmd == "udpForwarderStop")
        {
            RequireForwarder().StopForwarding(AddressField(command, "source"), AddressField(command, "destination"));
        }
        else if (cmd == "udpForwarderInfo")
        {
            result["maxEntries"] = RequireForwarder().GetMaxForwardEntries();
            result["usedEntries"] = RequireForwarder().GetUsedForwardEntries();
        }
        else if (cmd == "udpForwarderShutdown")
        {
            RequireForwarder().Shutdown();
        }
        else
        {
            return false;
        }
        return true;
    }

    bool RelayCommands::DispatchProxy(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "udpProxyCoordinatorAttach")
        {
            if (proxyCoordinator_ == nullptr)
            {
                proxyCoordinator_ = RakNet::UDPProxyCoordinator::GetInstance();
                peer_->AttachPlugin(proxyCoordinator_);
            }
            proxyCoordinator_->SetRemoteLoginPassword(Optional<std::string>(command, "password", "").c_str());
        }
        else if (cmd == "udpProxyServerAttach")
        {
            if (proxyServer_ == nullptr)
            {
                proxyServer_ = RakNet::UDPProxyServer::GetInstance();
                proxyServer_->SetResultHandler(recorder_.get());
                peer_->AttachPlugin(proxyServer_);
            }
            proxyServer_->SetServerPublicIP(Optional<std::string>(command, "publicIp", "").c_str());
            if (Has(command, "maxForwardEntries"))
                proxyServer_->udpForwarder.SetMaxForwardEntries(
                    static_cast<unsigned short>(Ranged(Required<long long>(command, "maxForwardEntries"), "maxForwardEntries", 1, 65535 / 2 - 1)));
        }
        else if (cmd == "udpProxyServerLogin")
        {
            // NATCompleteServer logs into a coordinator on the same peer at GetInternalID(UNASSIGNED_SYSTEM_ADDRESS, 0).
            RakNet::SystemAddress coordinator = Has(command, "coordinator") ? AddressField(command, "coordinator")
                                                                            : peer_->GetInternalID(RakNet::UNASSIGNED_SYSTEM_ADDRESS, 0);
            result["result"] = RequireProxyServer().LoginToCoordinator(Required<std::string>(command, "password").c_str(), coordinator);
        }
        else if (cmd == "udpProxyClientAttach")
        {
            if (proxyClient_ == nullptr)
            {
                proxyClient_ = RakNet::UDPProxyClient::GetInstance();
                proxyClient_->SetResultHandler(recorder_.get());
                peer_->AttachPlugin(proxyClient_);
            }
        }
        else if (cmd == "udpProxyClientRequest")
        {
            RakNet::UDPProxyClient& client = RequireProxyClient();
            RakNet::SystemAddress coordinator = AddressField(command, "coordinator");
            RakNet::SystemAddress source = Has(command, "source") ? AddressField(command, "source") : RakNet::UNASSIGNED_SYSTEM_ADDRESS;
            auto timeout = static_cast<RakNet::TimeMS>(Ranged(Required<long long>(command, "timeoutMs"), "timeoutMs", 0, std::numeric_limits<RakNet::TimeMS>::max()));
            RakNet::BitStream selection;
            std::vector<unsigned char> selectionBytes;
            if (!TryFromHex(Optional<std::string>(command, "serverSelection", ""), selectionBytes))
                throw PluginCommandError("field 'serverSelection' is not valid hex");
            if (!selectionBytes.empty())
                selection.WriteAlignedBytes(selectionBytes.data(), static_cast<unsigned int>(selectionBytes.size()));
            bool sent = Has(command, "targetAddress")
                            ? client.RequestForwarding(coordinator, source, AddressField(command, "targetAddress"), timeout, &selection)
                            : client.RequestForwarding(coordinator, source, GuidField(command, "target"), timeout, &selection);
            result["result"] = sent;
        }
        else if (cmd == "udpProxyCallbacks")
        {
            result["callbacks"] = recorder_->Calls();
        }
        else
        {
            return false;
        }
        return true;
    }

    bool RelayCommands::DispatchRouter2(const std::string& cmd, const json& command, ordered_json&)
    {
        if (cmd == "router2Attach")
        {
            if (router2_ == nullptr)
            {
                // No debug interface: Router2 formats its messages with %I64d, which only Windows' printf takes.
                router2_ = RakNet::Router2::GetInstance();
                peer_->AttachPlugin(router2_);
            }
            if (Has(command, "maxForwardingRequests"))
                router2_->SetMaximumForwardingRequests(static_cast<int>(Ranged(Required<long long>(command, "maxForwardingRequests"), "maxForwardingRequests", std::numeric_limits<int>::min(), std::numeric_limits<int>::max())));
        }
        else if (cmd == "router2EstablishRouting")
        {
            RequireRouter2().EstablishRouting(GuidField(command, "target"));
        }
        else
        {
            return false;
        }
        return true;
    }

    bool RelayCommands::DispatchRelay(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "relayAttach")
        {
            if (relay_ == nullptr)
            {
                relay_ = RakNet::RelayPlugin::GetInstance();
                peer_->AttachPlugin(relay_);
            }
            relay_->SetAcceptAddParticipantRequests(Optional<bool>(command, "acceptAddParticipantRequests", false));
        }
        else if (cmd == "relayAddParticipantOnServer")
        {
            result["result"] = RelayResultName(RequireRelay().AddParticipantOnServer(Required<std::string>(command, "key").c_str(), GuidField(command, "target")));
        }
        else if (cmd == "relayRemoveParticipantOnServer")
        {
            RequireRelay().RemoveParticipantOnServer(GuidField(command, "target"));
        }
        else if (cmd == "relayAddParticipantRequest")
        {
            RequireRelay().AddParticipantRequestFromClient(Required<std::string>(command, "key").c_str(), GuidField(command, "server"));
        }
        else if (cmd == "relayRemoveParticipantRequest")
        {
            RequireRelay().RemoveParticipantRequestFromClient(GuidField(command, "server"));
        }
        else if (cmd == "relaySendToParticipant")
        {
            RakNet::RelayPlugin& relay = RequireRelay();
            RakNet::BitStream data;
            ReadBits(command, data);
            relay.SendToParticipant(GuidField(command, "server"), Required<std::string>(command, "key").c_str(), &data, PriorityField(command),
                                    ReliabilityField(command), static_cast<char>(Ranged(Optional<long long>(command, "channel", 0), "channel", 0, 31)));
        }
        else if (cmd == "relaySendGroupMessage")
        {
            RakNet::RelayPlugin& relay = RequireRelay();
            RakNet::BitStream data;
            ReadBits(command, data);
            relay.SendGroupMessage(GuidField(command, "server"), &data, PriorityField(command), ReliabilityField(command),
                                   static_cast<char>(Ranged(Optional<long long>(command, "channel", 0), "channel", 0, 31)));
        }
        else if (cmd == "relayJoinGroup")
        {
            RequireRelay().JoinGroupRequest(GuidField(command, "server"), Required<std::string>(command, "group").c_str());
        }
        else if (cmd == "relayLeaveGroup")
        {
            RequireRelay().LeaveGroup(GuidField(command, "server"));
        }
        else if (cmd == "relayGetGroupList")
        {
            RequireRelay().GetGroupList(GuidField(command, "server"));
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::UDPForwarder& RelayCommands::RequireForwarder()
    {
        if (forwarder_ == nullptr)
            throw PluginCommandError("no UDPForwarder is running; send udpForwarderStartup first");
        return *forwarder_;
    }

    RakNet::UDPProxyClient& RelayCommands::RequireProxyClient()
    {
        if (proxyClient_ == nullptr)
            throw PluginCommandError("no UDPProxyClient is attached; send udpProxyClientAttach first");
        return *proxyClient_;
    }

    RakNet::UDPProxyServer& RelayCommands::RequireProxyServer()
    {
        if (proxyServer_ == nullptr)
            throw PluginCommandError("no UDPProxyServer is attached; send udpProxyServerAttach first");
        return *proxyServer_;
    }

    RakNet::Router2& RelayCommands::RequireRouter2()
    {
        if (router2_ == nullptr)
            throw PluginCommandError("no Router2 is attached; send router2Attach first");
        return *router2_;
    }

    RakNet::RelayPlugin& RelayCommands::RequireRelay()
    {
        if (relay_ == nullptr)
            throw PluginCommandError("no RelayPlugin is attached; send relayAttach first");
        return *relay_;
    }
}
