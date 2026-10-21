#pragma once

#include <nlohmann/json.hpp>

#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace RakNet
{
    struct SystemAddress;
    class HTTPConnection;
    class HTTPConnection2;
    class PacketizedTCP;
    class TCPInterface;
}

namespace interop
{
    // The tcp*, httpConnection* and http2* commands of docs/interop-protocol.md: a TCPInterface or PacketizedTCP next to
    // the peer, with HTTPConnection and HTTPConnection2 on it. A thread polls the interface as RakNet's samples do and
    // records what it reports until tcpPoll collects it. Errors are reported with PluginCommandError.
    class TcpCommands
    {
    public:
        TcpCommands();
        ~TcpCommands();

        TcpCommands(const TcpCommands&) = delete;
        TcpCommands& operator=(const TcpCommands&) = delete;

        // Runs cmd when it is one of these commands and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        void PollLoop();
        void PollOnce();
        void Record(const char* kind, const std::string& address);
        RakNet::TCPInterface& RequireTcp();
        void Start(const nlohmann::json& command, nlohmann::ordered_json& result);
        void Stop();
        RakNet::SystemAddress KnownAddress(const nlohmann::json& command, const char* field);
        void Info(const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchHttp(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        std::mutex mutex_;
        std::thread poller_;
        std::atomic<bool> stop_{false};
        RakNet::TCPInterface* tcp_ = nullptr;
        RakNet::PacketizedTCP* packetized_ = nullptr;
        RakNet::HTTPConnection* http_ = nullptr;
        RakNet::HTTPConnection2* http2_ = nullptr;
        nlohmann::ordered_json events_ = nlohmann::ordered_json::array();
    };
}
