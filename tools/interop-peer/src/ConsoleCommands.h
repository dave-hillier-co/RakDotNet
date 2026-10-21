#pragma once

#include <nlohmann/json.hpp>

#include <deque>
#include <string>

namespace RakNet
{
    class ConsoleServer;
    class LogCommandParser;
    class PacketConsoleLogger;
    class PluginInterface2;
    class RakNetCommandParser;
    class RakNetTransport2;
    class RakPeerInterface;
    class TelnetTransport;
    class TransportInterface;
}

namespace interop
{
    // The console* and packetConsoleLoggerAttach commands of docs/interop-protocol.md: a ConsoleServer on the peer, as
    // Samples/CommandConsoleServer and Samples/PacketConsoleLogger set one up, over RakNetTransport2 or TelnetTransport.
    // ConsoleServer::Update runs from a plugin's Update, so inside RakPeer::Receive, under the lock the peer's receive
    // loop and every command hold. Errors are reported with PluginCommandError.
    class ConsoleCommands
    {
    public:
        explicit ConsoleCommands(RakNet::RakPeerInterface* peer);
        ~ConsoleCommands();

        ConsoleCommands(const ConsoleCommands&) = delete;
        ConsoleCommands& operator=(const ConsoleCommands&) = delete;

        // Runs cmd when it is one of these commands and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        void Start(const nlohmann::json& command, nlohmann::ordered_json& result);
        RakNet::LogCommandParser& RequireLogParser();
        const char* Intern(const std::string& text);

        RakNet::RakPeerInterface* peer_;
        RakNet::ConsoleServer* console_ = nullptr;
        RakNet::RakNetCommandParser* rakNetParser_ = nullptr;
        RakNet::LogCommandParser* logParser_ = nullptr;
        RakNet::TelnetTransport* telnet_ = nullptr;
        RakNet::RakNetTransport2* rakNetTransport_ = nullptr;
        RakNet::PacketConsoleLogger* packetLogger_ = nullptr;
        RakNet::PluginInterface2* updater_ = nullptr;
        // LogCommandParser keeps the channel name pointers it is given (LogCommandParser.cpp:123), so they live here.
        std::deque<std::string> names_;
    };
}
