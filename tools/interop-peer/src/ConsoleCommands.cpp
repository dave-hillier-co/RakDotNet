#include "ConsoleCommands.h"

#include "PluginCommands.h"

#include "ConsoleServer.h"
#include "LogCommandParser.h"
#include "PacketConsoleLogger.h"
#include "PluginInterface2.h"
#include "RakNetCommandParser.h"
#include "RakNetTransport2.h"
#include "RakPeerInterface.h"
#include "TCPInterface.h"
#include "TelnetTransport.h"

#include <arpa/inet.h>
#include <limits>
#include <netinet/in.h>
#include <sys/socket.h>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    namespace
    {
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

        // TCPInterface keeps its listening socket protected; a pointer to the member, formed in a derived class, reads it.
        struct ListenSocketAccess : RakNet::TCPInterface
        {
            static __TCPSOCKET__ RakNet::TCPInterface::*Member() { return &ListenSocketAccess::listenSocket; }
        };

        // TelnetTransport keeps its TCPInterface protected and does not report the port it listens on.
        class ExposedTelnet : public RakNet::TelnetTransport
        {
        public:
            unsigned short ListenPort()
            {
                if (tcpInterface == 0)
                    return 0;
                __TCPSOCKET__ listenSocket = tcpInterface->*ListenSocketAccess::Member();
                if (listenSocket == 0)
                    return 0;
                sockaddr_in address;
                socklen_t length = sizeof(address);
                if (getsockname(listenSocket, reinterpret_cast<sockaddr*>(&address), &length) != 0)
                    return 0;
                return ntohs(address.sin_port);
            }
        };

        // Calls ConsoleServer::Update each time RakPeer::Receive updates its plugins, as a sample's main loop calls both.
        class ConsoleUpdater : public RakNet::PluginInterface2
        {
        public:
            explicit ConsoleUpdater(RakNet::ConsoleServer* console) : console_(console) {}

            void Update() override { console_->Update(); }

        private:
            RakNet::ConsoleServer* console_;
        };
    }

    ConsoleCommands::ConsoleCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    ConsoleCommands::~ConsoleCommands()
    {
        // The peer is destroyed first and never frees plugins; the console and its parts are freed here. Deleting the
        // TelnetTransport stops its TCPInterface, which closes descriptor 0 (TCPInterface.cpp:298-304), but the process
        // has finished reading stdin by now.
        delete updater_;
        delete packetLogger_;
        delete console_;
        delete rakNetParser_;
        delete logParser_;
        delete telnet_;
        delete rakNetTransport_;
    }

    bool ConsoleCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "consoleStart")
            Start(command, result);
        else if (cmd == "consoleLog")
        {
            std::string channel = Required<std::string>(command, "channel");
            std::string text = Required<std::string>(command, "text");
            RakNet::LogCommandParser& logParser = RequireLogParser();
            logParser.WriteLog(Intern(channel), "%s", text.c_str());
        }
        else if (cmd == "packetConsoleLoggerAttach")
        {
            RakNet::LogCommandParser& logParser = RequireLogParser();
            if (packetLogger_ == nullptr)
            {
                packetLogger_ = new RakNet::PacketConsoleLogger();
                packetLogger_->SetLogCommandParser(&logParser);
                peer_->AttachPlugin(packetLogger_);
            }
        }
        else
            return false;
        return true;
    }

    // Samples/CommandConsoleServer/main.cpp:33-68: the parsers, then the transport, the prompt, the peer and the channels.
    void ConsoleCommands::Start(const json& command, ordered_json& result)
    {
        if (console_ != nullptr)
            throw PluginCommandError("a console is already started");
        if (!peer_->IsActive())
            throw PluginCommandError("the peer has not been started");
        std::string transport = Required<std::string>(command, "transport");
        long long port = Optional<long long>(command, "port", 0);
        if (port < 0 || port > std::numeric_limits<unsigned short>::max())
            throw PluginCommandError("field 'port' is out of range");
        if (transport != "raknet" && transport != "telnet")
            throw PluginCommandError("field 'transport' must be raknet or telnet");
        json channels = Optional<json>(command, "channels", json::array());
        if (!channels.is_array())
            throw PluginCommandError("field 'channels' must be an array of strings");
        for (const json& channel : channels)
        {
            if (!channel.is_string())
                throw PluginCommandError("field 'channels' must be an array of strings");
        }

        console_ = new RakNet::ConsoleServer();
        if (Optional<bool>(command, "rakNetParser", true))
        {
            rakNetParser_ = new RakNet::RakNetCommandParser();
            console_->AddCommandParser(rakNetParser_);
        }
        if (Optional<bool>(command, "logParser", true))
        {
            logParser_ = new RakNet::LogCommandParser();
            console_->AddCommandParser(logParser_);
        }

        RakNet::TransportInterface* transportInterface;
        if (transport == "raknet")
        {
            rakNetTransport_ = new RakNet::RakNetTransport2();
            peer_->AttachPlugin(rakNetTransport_);
            transportInterface = rakNetTransport_;
        }
        else
            transportInterface = telnet_ = new ExposedTelnet();
        console_->SetTransportProvider(transportInterface, static_cast<unsigned short>(port));
        console_->SetPrompt(Optional<std::string>(command, "prompt", "").c_str());
        if (rakNetParser_ != nullptr)
            rakNetParser_->SetRakPeerInterface(peer_);
        if (logParser_ != nullptr)
        {
            for (const json& channel : channels)
                logParser_->AddChannel(Intern(channel.get<std::string>()));
        }

        updater_ = new ConsoleUpdater(console_);
        peer_->AttachPlugin(updater_);
        result["port"] = telnet_ != nullptr ? static_cast<ExposedTelnet*>(telnet_)->ListenPort() : 0;
    }

    RakNet::LogCommandParser& ConsoleCommands::RequireLogParser()
    {
        if (logParser_ == nullptr)
            throw PluginCommandError("no LogCommandParser; send consoleStart with logParser first");
        return *logParser_;
    }

    const char* ConsoleCommands::Intern(const std::string& text)
    {
        for (const std::string& name : names_)
        {
            if (name == text)
                return name.c_str();
        }
        names_.push_back(text);
        return names_.back().c_str();
    }
}
