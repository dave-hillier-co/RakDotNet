#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <string>

namespace RakNet
{
    class CloudClient;
    class CloudServer;
    class RakPeerInterface;
}

namespace interop
{
    class CloudFilter;
    class CloudRecorder;

    // The commands that attach and drive CloudClient and CloudServer on the peer, specified in
    // docs/interop-protocol.md. Commands and the plugins' callbacks both run with the peer's lock held (callbacks run
    // inside Receive()), so the records need no lock of their own.
    class CloudCommands
    {
    public:
        explicit CloudCommands(RakNet::RakPeerInterface* peer);
        ~CloudCommands();

        CloudCommands(const CloudCommands&) = delete;
        CloudCommands& operator=(const CloudCommands&) = delete;

        // Runs cmd when it is a cloud command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        // What the client's callback and the server's filter reported, in the order they reported it.
        struct Records
        {
            nlohmann::ordered_json gets = nlohmann::ordered_json::array();
            nlohmann::ordered_json notifications = nlohmann::ordered_json::array();
            nlohmann::ordered_json filterCalls = nlohmann::ordered_json::array();
        };

    private:
        RakNet::CloudClient& RequireClient();
        RakNet::CloudServer& RequireServer();

        RakNet::RakPeerInterface* peer_;
        RakNet::CloudClient* client_ = nullptr;
        RakNet::CloudServer* server_ = nullptr;
        std::unique_ptr<CloudRecorder> recorder_;
        std::unique_ptr<CloudFilter> filter_;
        Records records_;
    };
}
