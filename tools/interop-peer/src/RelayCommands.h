#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <string>
#include <vector>

namespace RakNet
{
    class RakPeerInterface;
    class RelayPlugin;
    class Router2;
    class UDPForwarder;
    class UDPProxyClient;
    class UDPProxyCoordinator;
    class UDPProxyServer;
}

namespace interop
{
    class ProxyResultRecorder;

    // The relaying commands of docs/interop-protocol.md: a UDPForwarder of the process's own, and the commands that
    // attach and drive UDPProxyClient, UDPProxyServer, UDPProxyCoordinator, Router2 and RelayPlugin. Errors are thrown
    // as PluginCommandError. Every method is called with the peer's lock held, the lock Receive() runs under, which is
    // also where the UDPProxy result handlers are called.
    class RelayCommands
    {
    public:
        explicit RelayCommands(RakNet::RakPeerInterface* peer);
        ~RelayCommands();

        RelayCommands(const RelayCommands&) = delete;
        RelayCommands& operator=(const RelayCommands&) = delete;

        // Runs cmd when it is a relaying command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        bool DispatchForwarder(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchProxy(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchRouter2(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchRelay(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        RakNet::UDPForwarder& RequireForwarder();
        RakNet::UDPProxyClient& RequireProxyClient();
        RakNet::UDPProxyServer& RequireProxyServer();
        RakNet::Router2& RequireRouter2();
        RakNet::RelayPlugin& RequireRelay();

        RakNet::RakPeerInterface* peer_;
        std::unique_ptr<ProxyResultRecorder> recorder_;
        RakNet::UDPForwarder* forwarder_ = nullptr;
        RakNet::UDPProxyClient* proxyClient_ = nullptr;
        RakNet::UDPProxyServer* proxyServer_ = nullptr;
        RakNet::UDPProxyCoordinator* proxyCoordinator_ = nullptr;
        RakNet::Router2* router2_ = nullptr;
        RakNet::RelayPlugin* relay_ = nullptr;
    };
}
