#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <string>
#include <vector>

namespace RakNet
{
    class RakPeerInterface;
    class TeamBalancer;
    class TeamManager;
    class TM_Team;
    class TM_TeamMember;
    class TM_World;
}

namespace interop
{
    // The commands that attach and drive TeamManager and TeamBalancer, specified in docs/interop-protocol.md. Errors are
    // reported as PluginCommandError. Every method is called with the peer's lock held, the lock Receive() runs under, so
    // plugin state is touched by one thread at a time. The peer owns the TM_Team and TM_TeamMember objects it makes.
    class TeamCommands
    {
    public:
        explicit TeamCommands(RakNet::RakPeerInterface* peer);
        ~TeamCommands();

        TeamCommands(const TeamCommands&) = delete;
        TeamCommands& operator=(const TeamCommands&) = delete;

        // Runs cmd when it is a team command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    private:
        bool DispatchTeamManager(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchTeamBalancer(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        RakNet::TeamManager& RequireTeamManager();
        RakNet::TeamBalancer& RequireTeamBalancer();
        RakNet::TM_World& RequireWorld(const nlohmann::json& command);
        RakNet::TM_Team& RequireTeam(RakNet::TM_World& world, const nlohmann::json& command, const char* field);
        RakNet::TM_TeamMember& RequireMember(RakNet::TM_World& world, const nlohmann::json& command);
        void WorldInfo(RakNet::TM_World& world, nlohmann::ordered_json& result);

        RakNet::RakPeerInterface* peer_;
        RakNet::TeamManager* teamManager_ = nullptr;
        RakNet::TeamBalancer* teamBalancer_ = nullptr;
        std::vector<std::unique_ptr<RakNet::TM_Team>> teams_;
        std::vector<std::unique_ptr<RakNet::TM_TeamMember>> members_;
    };
}
