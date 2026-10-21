#pragma once

#include <nlohmann/json.hpp>

#include <string>

namespace RakNet
{
    class ConnectionGraph2;
    class FullyConnectedMesh2;
    class RakPeerInterface;
}

namespace interop
{
    // The commands that attach and drive ConnectionGraph2 and FullyConnectedMesh2, specified in
    // docs/interop-protocol.md. Errors are reported as PluginCommandError. Every method is called with the peer's
    // lock held, the lock Receive() runs under, so plugin state is touched by one thread at a time.
    class TopologyCommands
    {
    public:
        explicit TopologyCommands(RakNet::RakPeerInterface* peer);
        ~TopologyCommands();

        TopologyCommands(const TopologyCommands&) = delete;
        TopologyCommands& operator=(const TopologyCommands&) = delete;

        // Runs cmd when it is a topology command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        bool DispatchConnectionGraph(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchMesh(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        RakNet::ConnectionGraph2& RequireConnectionGraph();
        RakNet::FullyConnectedMesh2& RequireMesh();
        void ConnectionGraphInfo(nlohmann::ordered_json& result);
        void MeshInfo(nlohmann::ordered_json& result);

        RakNet::RakPeerInterface* peer_;
        RakNet::ConnectionGraph2* connectionGraph_ = nullptr;
        RakNet::FullyConnectedMesh2* mesh_ = nullptr;
    };
}
