#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <string>

namespace RakNet
{
    class NatPunchthroughClient;
    class NatPunchthroughServer;
    class NatTypeDetectionClient;
    class RakPeerInterface;
    struct NatPunchthroughDebugInterface;
}

namespace interop
{
    class ObservableNatTypeDetectionServer;

    // The NAT commands of docs/interop-protocol.md: natGateway, which puts the process behind a NAT router of the test
    // harness, and the commands that attach and drive NatTypeDetectionClient, NatTypeDetectionServer,
    // NatPunchthroughClient and NatPunchthroughServer. Errors are thrown as PluginCommandError. Every method is called
    // with the peer's lock held, the lock Receive() runs under.
    class NatCommands
    {
    public:
        explicit NatCommands(RakNet::RakPeerInterface* peer);
        ~NatCommands();

        NatCommands(const NatCommands&) = delete;
        NatCommands& operator=(const NatCommands&) = delete;

        // Runs cmd when it is a NAT command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        RakNet::NatTypeDetectionClient& RequireTypeDetectionClient();
        ObservableNatTypeDetectionServer& RequireTypeDetectionServer();
        RakNet::NatPunchthroughClient& RequirePunchthroughClient();
        void Configure(const nlohmann::json& command);

        RakNet::RakPeerInterface* peer_;
        RakNet::NatTypeDetectionClient* typeDetectionClient_ = nullptr;
        ObservableNatTypeDetectionServer* typeDetectionServer_ = nullptr;
        RakNet::NatPunchthroughClient* punchthroughClient_ = nullptr;
        RakNet::NatPunchthroughServer* punchthroughServer_ = nullptr;
        std::unique_ptr<RakNet::NatPunchthroughDebugInterface> debug_;
    };
}
