#include "TeamCommands.h"

#include "Hex.h"
#include "PluginCommands.h"

#include "BitStream.h"
#include "DS_List.h"
#include "RakPeerInterface.h"
#include "TeamBalancer.h"
#include "TeamManager.h"

#include <cerrno>
#include <cstdint>
#include <cstdlib>

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

        template <typename T>
        T InRange(const json& command, const char* field, long long low, long long high)
        {
            long long value = Required<long long>(command, field);
            if (value < low || value > high)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<T>(value);
        }

        // A 64-bit value (RakNetGUID or NetworkID) as a decimal string, or a JSON number.
        std::uint64_t Unsigned64Field(const json& command, const char* field)
        {
            const json value = Required<json>(command, field);
            if (value.is_number_unsigned())
                return value.get<std::uint64_t>();
            if (!value.is_string())
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal string");
            const std::string text = value.get<std::string>();
            if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal string");
            errno = 0;
            unsigned long long parsed = std::strtoull(text.c_str(), nullptr, 10);
            if (errno == ERANGE)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<std::uint64_t>(parsed);
        }

        bool Present(const json& command, const char* field)
        {
            auto it = command.find(field);
            return it != command.end() && !it->is_null();
        }

        std::string Decimal(std::uint64_t value)
        {
            return std::to_string(value);
        }

        const char* JoinTeamTypeName(RakNet::JoinTeamType type)
        {
            switch (type)
            {
            case RakNet::JOIN_ANY_AVAILABLE_TEAM: return "JOIN_ANY_AVAILABLE_TEAM";
            case RakNet::JOIN_SPECIFIC_TEAM: return "JOIN_SPECIFIC_TEAM";
            case RakNet::JOIN_NO_TEAM: return "JOIN_NO_TEAM";
            }
            return "JOIN_NO_TEAM";
        }

        ordered_json TeamIds(const DataStructures::List<RakNet::TM_Team*>& teams)
        {
            ordered_json array = ordered_json::array();
            for (unsigned i = 0; i < teams.Size(); ++i)
                array.push_back(Decimal(teams[i]->GetNetworkID()));
            return array;
        }

        std::vector<unsigned char> DataField(const json& command)
        {
            std::vector<unsigned char> bytes;
            if (!TryFromHex(Required<std::string>(command, "data"), bytes))
                throw PluginCommandError("field 'data' is not valid hex");
            return bytes;
        }

        // The world ID and NetworkID that a construction bitstream starts with.
        void PeekConstruction(const std::vector<unsigned char>& data, RakNet::WorldId& worldId, RakNet::NetworkID& networkId)
        {
            RakNet::BitStream bits(const_cast<unsigned char*>(data.data()), static_cast<unsigned>(data.size()), false);
            if (!bits.Read(worldId) || !bits.Read(networkId))
                throw PluginCommandError("field 'data' is too short for a construction");
        }
    }

    TeamCommands::TeamCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    TeamCommands::~TeamCommands()
    {
        // The peer is destroyed first and never frees plugins. Members go first: dereferencing one sends nothing, and a
        // team without members leaves its world without sending either.
        members_.clear();
        teams_.clear();
        if (teamManager_ != nullptr)
            RakNet::TeamManager::DestroyInstance(teamManager_);
        if (teamBalancer_ != nullptr)
            RakNet::TeamBalancer::DestroyInstance(teamBalancer_);
    }

    bool TeamCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        return DispatchTeamManager(cmd, command, result) || DispatchTeamBalancer(cmd, command, result);
    }

    bool TeamCommands::DispatchTeamManager(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "tmAttach")
        {
            if (teamManager_ == nullptr)
            {
                teamManager_ = RakNet::TeamManager::GetInstance();
                const std::string topology = Optional<std::string>(command, "topology", "TM_PEER_TO_PEER");
                if (topology == "TM_CLIENT_SERVER")
                    teamManager_->SetTopology(RakNet::TM_CLIENT_SERVER);
                else if (topology != "TM_PEER_TO_PEER")
                    throw PluginCommandError("field 'topology' must be TM_PEER_TO_PEER or TM_CLIENT_SERVER");
                peer_->AttachPlugin(teamManager_);
            }
        }
        else if (cmd == "tmSetAutoManageConnections")
        {
            RequireTeamManager().SetAutoManageConnections(Required<bool>(command, "enabled"));
        }
        else if (cmd == "tmAddWorld")
        {
            RakNet::TeamManager& manager = RequireTeamManager();
            RakNet::WorldId worldId = InRange<RakNet::WorldId>(command, "worldId", 0, 254);
            if (manager.GetWorldWithId(worldId) != nullptr)
                throw PluginCommandError("world is already in use");
            manager.AddWorld(worldId);
        }
        else if (cmd == "tmSetHost")
        {
            RequireWorld(command).SetHost(RakNet::RakNetGUID(Unsigned64Field(command, "host")));
        }
        else if (cmd == "tmAddParticipant")
        {
            RequireWorld(command).AddParticipant(RakNet::RakNetGUID(Unsigned64Field(command, "target")));
        }
        else if (cmd == "tmRemoveParticipant")
        {
            RequireWorld(command).RemoveParticipant(RakNet::RakNetGUID(Unsigned64Field(command, "target")));
        }
        else if (cmd == "tmReferenceTeam")
        {
            RakNet::TM_World& world = RequireWorld(command);
            teams_.push_back(std::unique_ptr<RakNet::TM_Team>(new RakNet::TM_Team()));
            world.ReferenceTeam(teams_.back().get(), Unsigned64Field(command, "team"), Optional<bool>(command, "applyBalancing", true));
        }
        else if (cmd == "tmReferenceTeamMember")
        {
            RakNet::TM_World& world = RequireWorld(command);
            members_.push_back(std::unique_ptr<RakNet::TM_TeamMember>(new RakNet::TM_TeamMember()));
            world.ReferenceTeamMember(members_.back().get(), Unsigned64Field(command, "member"));
        }
        else if (cmd == "tmDereferenceTeam")
        {
            RakNet::TM_World& world = RequireWorld(command);
            world.DereferenceTeam(&RequireTeam(world, command, "team"), InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255));
        }
        else if (cmd == "tmDereferenceTeamMember")
        {
            RakNet::TM_World& world = RequireWorld(command);
            world.DereferenceTeamMember(&RequireMember(world, command));
        }
        else if (cmd == "tmRequestTeam")
        {
            RakNet::TM_World& world = RequireWorld(command);
            RakNet::TM_TeamMember& member = RequireMember(world, command);
            const std::string selection = Required<std::string>(command, "selection");
            if (selection == "JOIN_ANY_AVAILABLE_TEAM")
                result["result"] = member.RequestTeam(RakNet::TeamSelection::AnyAvailable());
            else if (selection == "JOIN_SPECIFIC_TEAM")
                result["result"] = member.RequestTeam(RakNet::TeamSelection::SpecificTeam(&RequireTeam(world, command, "team")));
            else if (selection == "JOIN_NO_TEAM")
                result["result"] = member.RequestTeam(RakNet::TeamSelection::NoTeam(InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255)));
            else
                throw PluginCommandError("field 'selection' must be a JoinTeamType name");
        }
        else if (cmd == "tmRequestTeamSwitch")
        {
            RakNet::TM_World& world = RequireWorld(command);
            RakNet::TM_TeamMember& member = RequireMember(world, command);
            RakNet::TM_Team* teamToLeave = Present(command, "teamToLeave") ? &RequireTeam(world, command, "teamToLeave") : nullptr;
            result["result"] = member.RequestTeamSwitch(&RequireTeam(world, command, "teamToJoin"), teamToLeave);
        }
        else if (cmd == "tmCancelTeamRequest")
        {
            RakNet::TM_World& world = RequireWorld(command);
            RakNet::TM_TeamMember& member = RequireMember(world, command);
            result["result"] = member.CancelTeamRequest(Present(command, "team") ? &RequireTeam(world, command, "team") : nullptr);
        }
        else if (cmd == "tmLeaveTeam")
        {
            RakNet::TM_World& world = RequireWorld(command);
            RakNet::TM_TeamMember& member = RequireMember(world, command);
            result["result"] = member.LeaveTeam(&RequireTeam(world, command, "team"), InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255));
        }
        else if (cmd == "tmLeaveAllTeams")
        {
            RakNet::TM_World& world = RequireWorld(command);
            result["result"] = RequireMember(world, command).LeaveAllTeams(InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255));
        }
        else if (cmd == "tmSetMemberLimit")
        {
            RakNet::TM_World& world = RequireWorld(command);
            result["result"] = RequireTeam(world, command, "team").SetMemberLimit(
                InRange<RakNet::TeamMemberLimit>(command, "limit", 0, 65535), InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255));
        }
        else if (cmd == "tmSetJoinPermissions")
        {
            RakNet::TM_World& world = RequireWorld(command);
            result["result"] = RequireTeam(world, command, "team").SetJoinPermissions(
                InRange<RakNet::JoinPermissions>(command, "permissions", 0, 255));
        }
        else if (cmd == "tmSetBalanceTeams")
        {
            result["result"] = RequireWorld(command).SetBalanceTeams(
                Required<bool>(command, "enabled"), InRange<RakNet::NoTeamId>(command, "noTeamId", 0, 255));
        }
        else if (cmd == "tmSerializeTeam" || cmd == "tmSerializeTeamMember")
        {
            RakNet::TM_World& world = RequireWorld(command);
            RakNet::BitStream bits;
            if (cmd == "tmSerializeTeam")
                RequireTeam(world, command, "team").SerializeConstruction(&bits);
            else
                RequireMember(world, command).SerializeConstruction(&bits);
            result["data"] = ToHex(bits.GetData(), bits.GetNumberOfBytesUsed());
        }
        else if (cmd == "tmDeserializeTeam")
        {
            // Into the team already referenced with that NetworkID, as the sample's static teams; otherwise a new one.
            RakNet::TeamManager& manager = RequireTeamManager();
            std::vector<unsigned char> data = DataField(command);
            RakNet::WorldId worldId;
            RakNet::NetworkID networkId;
            PeekConstruction(data, worldId, networkId);
            RakNet::TM_World* world = manager.GetWorldWithId(worldId);
            if (world == nullptr)
                throw PluginCommandError("no world with the construction's world ID");
            RakNet::TM_Team* team = world->GetTeamByNetworkID(networkId);
            if (team == nullptr)
            {
                teams_.push_back(std::unique_ptr<RakNet::TM_Team>(new RakNet::TM_Team()));
                team = teams_.back().get();
            }
            RakNet::BitStream bits(data.data(), static_cast<unsigned>(data.size()), false);
            result["result"] = team->DeserializeConstruction(&manager, &bits);
        }
        else if (cmd == "tmDeserializeTeamMember")
        {
            RakNet::TeamManager& manager = RequireTeamManager();
            std::vector<unsigned char> data = DataField(command);
            RakNet::WorldId worldId;
            RakNet::NetworkID networkId;
            PeekConstruction(data, worldId, networkId);
            if (manager.GetWorldWithId(worldId) == nullptr)
                throw PluginCommandError("no world with the construction's world ID");
            members_.push_back(std::unique_ptr<RakNet::TM_TeamMember>(new RakNet::TM_TeamMember()));
            RakNet::BitStream bits(data.data(), static_cast<unsigned>(data.size()), false);
            result["result"] = members_.back()->DeserializeConstruction(&manager, &bits);
        }
        else if (cmd == "tmInfo")
        {
            WorldInfo(RequireWorld(command), result);
        }
        else
        {
            return false;
        }
        return true;
    }

    bool TeamCommands::DispatchTeamBalancer(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "tbAttach")
        {
            if (teamBalancer_ == nullptr)
            {
                teamBalancer_ = RakNet::TeamBalancer::GetInstance();
                peer_->AttachPlugin(teamBalancer_);
            }
        }
        else if (cmd == "tbSetTeamSizeLimit")
        {
            RequireTeamBalancer().SetTeamSizeLimit(InRange<RakNet::TeamId>(command, "team", 0, 254), InRange<unsigned short>(command, "limit", 0, 65535));
        }
        else if (cmd == "tbSetDefaultAssignmentAlgorithm")
        {
            const std::string algorithm = Required<std::string>(command, "algorithm");
            if (algorithm == "SMALLEST_TEAM")
                RequireTeamBalancer().SetDefaultAssignmentAlgorithm(RakNet::TeamBalancer::SMALLEST_TEAM);
            else if (algorithm == "FILL_IN_ORDER")
                RequireTeamBalancer().SetDefaultAssignmentAlgorithm(RakNet::TeamBalancer::FILL_IN_ORDER);
            else
                throw PluginCommandError("field 'algorithm' must be SMALLEST_TEAM or FILL_IN_ORDER");
        }
        else if (cmd == "tbSetForceEvenTeams")
        {
            RequireTeamBalancer().SetForceEvenTeams(Required<bool>(command, "force"));
        }
        else if (cmd == "tbSetLockTeams")
        {
            RequireTeamBalancer().SetLockTeams(Required<bool>(command, "lock"));
        }
        else if (cmd == "tbRequestSpecificTeam")
        {
            RequireTeamBalancer().RequestSpecificTeam(Unsigned64Field(command, "member"), InRange<RakNet::TeamId>(command, "team", 0, 255));
        }
        else if (cmd == "tbCancelRequestSpecificTeam")
        {
            RequireTeamBalancer().CancelRequestSpecificTeam(Unsigned64Field(command, "member"));
        }
        else if (cmd == "tbRequestAnyTeam")
        {
            RequireTeamBalancer().RequestAnyTeam(Unsigned64Field(command, "member"));
        }
        else if (cmd == "tbGetMyTeam")
        {
            result["team"] = static_cast<unsigned>(RequireTeamBalancer().GetMyTeam(Unsigned64Field(command, "member")));
        }
        else if (cmd == "tbDeleteMember")
        {
            RequireTeamBalancer().DeleteMember(Unsigned64Field(command, "member"));
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::TeamManager& TeamCommands::RequireTeamManager()
    {
        if (teamManager_ == nullptr)
            throw PluginCommandError("no TeamManager is attached; send tmAttach first");
        return *teamManager_;
    }

    RakNet::TeamBalancer& TeamCommands::RequireTeamBalancer()
    {
        if (teamBalancer_ == nullptr)
            throw PluginCommandError("no TeamBalancer is attached; send tbAttach first");
        return *teamBalancer_;
    }

    RakNet::TM_World& TeamCommands::RequireWorld(const json& command)
    {
        RakNet::TM_World* world = RequireTeamManager().GetWorldWithId(InRange<RakNet::WorldId>(command, "worldId", 0, 254));
        if (world == nullptr)
            throw PluginCommandError("no such world; send tmAddWorld first");
        return *world;
    }

    RakNet::TM_Team& TeamCommands::RequireTeam(RakNet::TM_World& world, const json& command, const char* field)
    {
        RakNet::TM_Team* team = world.GetTeamByNetworkID(Unsigned64Field(command, field));
        if (team == nullptr)
            throw PluginCommandError(std::string("no team with the NetworkID in '") + field + "'");
        return *team;
    }

    RakNet::TM_TeamMember& TeamCommands::RequireMember(RakNet::TM_World& world, const json& command)
    {
        RakNet::TM_TeamMember* member = world.GetTeamMemberByNetworkID(Unsigned64Field(command, "member"));
        if (member == nullptr)
            throw PluginCommandError("no team member with the NetworkID in 'member'");
        return *member;
    }

    void TeamCommands::WorldInfo(RakNet::TM_World& world, ordered_json& result)
    {
        result["host"] = Decimal(world.GetHost().g);
        result["balanceTeams"] = world.GetBalanceTeams();
        DataStructures::List<RakNet::RakNetGUID> participants;
        world.GetParticipantList(participants);
        ordered_json participantArray = ordered_json::array();
        for (unsigned i = 0; i < participants.Size(); ++i)
            participantArray.push_back(Decimal(participants[i].g));
        result["participants"] = participantArray;

        ordered_json teams = ordered_json::array();
        for (unsigned i = 0; i < world.GetTeamCount(); ++i)
        {
            RakNet::TM_Team* team = world.GetTeamByIndex(i);
            ordered_json entry;
            entry["team"] = Decimal(team->GetNetworkID());
            ordered_json members = ordered_json::array();
            for (unsigned j = 0; j < team->GetTeamMembersCount(); ++j)
                members.push_back(Decimal(team->GetTeamMemberByIndex(j)->GetNetworkID()));
            entry["members"] = members;
            entry["memberLimit"] = team->GetMemberLimit();
            entry["memberLimitSetting"] = team->GetMemberLimitSetting();
            entry["joinPermissions"] = static_cast<unsigned>(team->GetJoinPermissions());
            entry["balancingApplies"] = team->GetBalancingApplies();
            teams.push_back(entry);
        }
        result["teams"] = teams;

        ordered_json members = ordered_json::array();
        for (unsigned i = 0; i < world.GetTeamMemberCount(); ++i)
        {
            RakNet::TM_TeamMember* member = world.GetTeamMemberByIndex(i);
            ordered_json entry;
            entry["member"] = Decimal(member->GetNetworkID());
            DataStructures::List<RakNet::TM_Team*> current;
            member->GetCurrentTeams(current);
            entry["teams"] = TeamIds(current);
            DataStructures::List<RakNet::TM_Team*> last;
            member->GetLastTeams(last);
            entry["lastTeams"] = TeamIds(last);
            entry["noTeamId"] = static_cast<unsigned>(member->GetNoTeamId());
            DataStructures::List<RakNet::TM_Team*> requested;
            member->GetRequestedSpecificTeams(requested);
            entry["requestedTeams"] = TeamIds(requested);
            RakNet::TeamSelection selection = member->GetRequestedTeam();
            entry["requestedTeam"] = JoinTeamTypeName(selection.joinTeamType);
            members.push_back(entry);
        }
        result["members"] = members;
    }
}
