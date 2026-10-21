#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <string>

namespace RakNet
{
    class InteropDynDNS;
    class InteropRackspace;
    class TCPInterface;
}

namespace interop
{
    class RackspaceRecorder;

    // The email*, dynDns* and rackspace* commands of docs/interop-protocol.md: RakNet's external services, as
    // Samples/SendEmail and Samples/RackspaceConsole use them, pointed at servers the test runs. EmailSender::Send blocks the
    // command until it is done; DynDNS and Rackspace are moved on by the commands that poll them. DynDNS and
    // Rackspace are copies compiled with their connections redirected (ServiceRedirects.cpp). Errors are reported with
    // PluginCommandError.
    class ServiceCommands
    {
    public:
        ServiceCommands();
        ~ServiceCommands();

        ServiceCommands(const ServiceCommands&) = delete;
        ServiceCommands& operator=(const ServiceCommands&) = delete;

        // Runs cmd when it is one of these commands and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        void EmailSend(const nlohmann::json& command, nlohmann::ordered_json& result);
        void DynDnsStart(const nlohmann::json& command);
        void DynDnsUpdate(nlohmann::ordered_json& result);
        void RackspaceAuthenticate(const nlohmann::json& command, nlohmann::ordered_json& result);
        void RackspaceOperation(const nlohmann::json& command);
        void RackspacePoll(nlohmann::ordered_json& result);

        RakNet::InteropDynDNS* dynDns_ = nullptr;
        bool dynDnsStarted_ = false;

        RakNet::TCPInterface* rackspaceTcp_ = nullptr;
        RakNet::InteropRackspace* rackspace_ = nullptr;
        std::unique_ptr<RackspaceRecorder> recorder_;
    };
}
