#include "TopologyCommands.h"

#include "Hex.h"
#include "PluginCommands.h"

#include "BitStream.h"
#include "ConnectionGraph2.h"
#include "DS_OrderedList.h"
#include "FullyConnectedMesh2.h"
#include "RakMemoryOverride.h"
#include "RakPeerInterface.h"

#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <vector>

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

        RakNet::RakNetGUID GuidField(const json& command, const char* field)
        {
            const json value = Required<json>(command, field);
            if (value.is_number_unsigned())
                return RakNet::RakNetGUID(value.get<std::uint64_t>());
            if (!value.is_string())
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            const std::string text = value.get<std::string>();
            if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            errno = 0;
            unsigned long long g = std::strtoull(text.c_str(), nullptr, 10);
            if (errno == ERANGE)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return RakNet::RakNetGUID(static_cast<std::uint64_t>(g));
        }

        std::vector<unsigned char> HexField(const json& command, const char* field, bool required)
        {
            std::string text = required ? Required<std::string>(command, field) : Optional<std::string>(command, field, "");
            std::vector<unsigned char> bytes;
            if (!TryFromHex(text, bytes))
                throw PluginCommandError(std::string("field '") + field + "' is not valid hex");
            return bytes;
        }

        // "a.b.c.d:port", the protocol's address form.
        RakNet::SystemAddress AddressField(const json& command, const char* field)
        {
            const std::string text = Required<std::string>(command, field);
            std::size_t colon = text.rfind(':');
            if (colon == std::string::npos || colon + 1 == text.size())
                throw PluginCommandError(std::string("field '") + field + "' must be an address with a port");
            errno = 0;
            unsigned long port = std::strtoul(text.c_str() + colon + 1, nullptr, 10);
            RakNet::SystemAddress address;
            if (errno == ERANGE || port > 65535 ||
                !address.FromStringExplicitPort(text.substr(0, colon).c_str(), static_cast<unsigned short>(port)))
                throw PluginCommandError(std::string("field '") + field + "' is not a valid address");
            return address;
        }

        std::string GuidText(const RakNet::RakNetGUID& guid)
        {
            return std::to_string(guid.g);
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                return "";
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        ordered_json GuidArray(const DataStructures::List<RakNet::RakNetGUID>& guids)
        {
            ordered_json array = ordered_json::array();
            for (unsigned i = 0; i < guids.Size(); ++i)
                array.push_back(GuidText(guids[i]));
            return array;
        }

        // A packet as the driver received it, rebuilt for the FullyConnectedMesh2 calls that take one.
        class PacketFromCommand
        {
        public:
            PacketFromCommand(RakNet::RakPeerInterface* peer, const json& command) : peer_(peer)
            {
                std::vector<unsigned char> data = HexField(command, "data", true);
                if (data.empty())
                    throw PluginCommandError("field 'data' must contain at least the message ID byte");
                packet_ = peer_->AllocatePacket(static_cast<unsigned>(data.size()));
                std::memcpy(packet_->data, data.data(), data.size());
                packet_->guid = GuidField(command, "guid");
                auto address = command.find("address");
                packet_->systemAddress = address == command.end() || address->is_null()
                    ? RakNet::UNASSIGNED_SYSTEM_ADDRESS
                    : AddressField(command, "address");
            }

            ~PacketFromCommand() { peer_->DeallocatePacket(packet_); }

            PacketFromCommand(const PacketFromCommand&) = delete;
            PacketFromCommand& operator=(const PacketFromCommand&) = delete;

            RakNet::Packet* Get() const { return packet_; }

        private:
            RakNet::RakPeerInterface* peer_;
            RakNet::Packet* packet_ = nullptr;
        };

        // FCM2VerifiedJoinSimultaneousTest.cpp overrides the user data hooks; this writes fixed bytes from fcm2Attach.
        class MeshWithUserData : public RakNet::FullyConnectedMesh2
        {
        public:
            MeshWithUserData(const std::vector<unsigned char>& joinStartUserData, const std::vector<unsigned char>& joinCapableUserData)
                : joinStartUserData_(joinStartUserData), joinCapableUserData_(joinCapableUserData)
            {
            }

            virtual void WriteVJCUserData(RakNet::BitStream* bsOut)
            {
                if (!joinCapableUserData_.empty())
                    bsOut->Write(reinterpret_cast<const char*>(joinCapableUserData_.data()), static_cast<unsigned>(joinCapableUserData_.size()));
            }

            virtual void WriteVJSUserData(RakNet::BitStream* bsOut, RakNet::RakNetGUID userGuid)
            {
                (void)userGuid;
                if (!joinStartUserData_.empty())
                    bsOut->Write(reinterpret_cast<const char*>(joinStartUserData_.data()), static_cast<unsigned>(joinStartUserData_.size()));
            }

        private:
            std::vector<unsigned char> joinStartUserData_;
            std::vector<unsigned char> joinCapableUserData_;
        };
    }

    TopologyCommands::TopologyCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    TopologyCommands::~TopologyCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here.
        if (connectionGraph_ != nullptr)
            RakNet::ConnectionGraph2::DestroyInstance(connectionGraph_);
        if (mesh_ != nullptr)
            delete mesh_;
    }

    bool TopologyCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        return DispatchConnectionGraph(cmd, command, result) || DispatchMesh(cmd, command, result);
    }

    bool TopologyCommands::DispatchConnectionGraph(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "cg2Attach")
        {
            if (connectionGraph_ == nullptr)
            {
                connectionGraph_ = RakNet::ConnectionGraph2::GetInstance();
                connectionGraph_->SetAutoProcessNewConnections(Optional<bool>(command, "autoProcess", true));
                peer_->AttachPlugin(connectionGraph_);
            }
        }
        else if (cmd == "cg2SetAutoProcess")
        {
            RequireConnectionGraph().SetAutoProcessNewConnections(Required<bool>(command, "enabled"));
        }
        else if (cmd == "cg2AddParticipant")
        {
            RakNet::RakNetGUID guid = GuidField(command, "target");
            RequireConnectionGraph().AddParticipant(peer_->GetSystemAddressFromGuid(guid), guid);
        }
        else if (cmd == "cg2Info")
        {
            ConnectionGraphInfo(result);
        }
        else
        {
            return false;
        }
        return true;
    }

    bool TopologyCommands::DispatchMesh(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "fcm2Attach")
        {
            if (mesh_ == nullptr)
            {
                mesh_ = new MeshWithUserData(HexField(command, "vjsUserData", false), HexField(command, "vjcUserData", false));
                mesh_->SetAutoparticipateConnections(Optional<bool>(command, "autoParticipate", true));
                std::vector<unsigned char> password = HexField(command, "password", false);
                mesh_->SetConnectOnNewRemoteConnection(
                    Optional<bool>(command, "connectOnNewRemote", true),
                    RakNet::RakString::NonVariadic(std::string(password.begin(), password.end()).c_str()));
                peer_->AttachPlugin(mesh_);
            }
        }
        else if (cmd == "fcm2AddParticipant")
        {
            RequireMesh().AddParticipant(GuidField(command, "target"));
        }
        else if (cmd == "fcm2ResetHostCalculation")
        {
            RequireMesh().ResetHostCalculation();
        }
        else if (cmd == "fcm2Clear")
        {
            RequireMesh().Clear();
        }
        else if (cmd == "fcm2Info")
        {
            MeshInfo(result);
        }
        else if (cmd == "fcm2StartVerifiedJoin")
        {
            RequireMesh().StartVerifiedJoin(GuidField(command, "target"));
        }
        else if (cmd == "fcm2RespondOnVerifiedJoinCapable")
        {
            RakNet::FullyConnectedMesh2& mesh = RequireMesh();
            PacketFromCommand packet(peer_, command);
            std::vector<unsigned char> additional = HexField(command, "additionalData", false);
            RakNet::BitStream additionalData;
            additionalData.Write(reinterpret_cast<const char*>(additional.data()), static_cast<unsigned>(additional.size()));
            mesh.RespondOnVerifiedJoinCapable(packet.Get(), Required<bool>(command, "accept"), additional.empty() ? nullptr : &additionalData);
        }
        else if (cmd == "fcm2VerifiedJoinRequired")
        {
            DataStructures::List<RakNet::SystemAddress> addresses;
            DataStructures::List<RakNet::RakNetGUID> guids;
            DataStructures::List<RakNet::BitStream*> userData;
            RequireMesh().GetVerifiedJoinRequiredProcessingList(GuidField(command, "host"), addresses, guids, userData);
            ordered_json systems = ordered_json::array();
            for (unsigned i = 0; i < guids.Size(); ++i)
            {
                ordered_json system;
                system["guid"] = GuidText(guids[i]);
                system["address"] = AddressText(addresses[i]);
                RakNet::BitStream* bits = i < userData.Size() ? userData[i] : nullptr;
                system["userData"] = bits == nullptr ? std::string() : ToHex(bits->GetData(), bits->GetNumberOfBytesUsed());
                system["userDataBits"] = bits == nullptr ? 0u : static_cast<unsigned>(bits->GetNumberOfBitsUsed());
                systems.push_back(system);
            }
            result["systems"] = systems;
        }
        else if (cmd == "fcm2VerifiedJoinAccepted")
        {
            RakNet::FullyConnectedMesh2& mesh = RequireMesh();
            PacketFromCommand packet(peer_, command);
            bool thisSystemAccepted = false;
            DataStructures::List<RakNet::RakNetGUID> systemsAccepted;
            RakNet::BitStream additionalData;
            mesh.GetVerifiedJoinAcceptedAdditionalData(packet.Get(), &thisSystemAccepted, systemsAccepted, &additionalData);
            result["thisSystemAccepted"] = thisSystemAccepted;
            result["systemsAccepted"] = GuidArray(systemsAccepted);
            result["additionalData"] = ToHex(additionalData.GetData(), additionalData.GetNumberOfBytesUsed());
        }
        else if (cmd == "fcm2ConnectToRemoteNewIncomingConnections")
        {
            RakNet::FullyConnectedMesh2& mesh = RequireMesh();
            PacketFromCommand packet(peer_, command);
            mesh.ConnectToRemoteNewIncomingConnections(packet.Get());
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::ConnectionGraph2& TopologyCommands::RequireConnectionGraph()
    {
        if (connectionGraph_ == nullptr)
            throw PluginCommandError("no ConnectionGraph2 is attached; send cg2Attach first");
        return *connectionGraph_;
    }

    RakNet::FullyConnectedMesh2& TopologyCommands::RequireMesh()
    {
        if (mesh_ == nullptr)
            throw PluginCommandError("no FullyConnectedMesh2 is attached; send fcm2Attach first");
        return *mesh_;
    }

    void TopologyCommands::ConnectionGraphInfo(ordered_json& result)
    {
        RakNet::ConnectionGraph2& graph = RequireConnectionGraph();
        DataStructures::OrderedList<RakNet::RakNetGUID, RakNet::RakNetGUID> participants;
        graph.GetParticipantList(participants);
        ordered_json participantArray = ordered_json::array();
        for (unsigned i = 0; i < participants.Size(); ++i)
        {
            ordered_json participant;
            participant["guid"] = GuidText(participants[i]);
            const unsigned capacity = 64;
            RakNet::SystemAddress addresses[capacity];
            RakNet::RakNetGUID guids[capacity];
            unsigned length = capacity;
            ordered_json connections = ordered_json::array();
            if (graph.GetConnectionListForRemoteSystem(participants[i], addresses, guids, &length))
            {
                for (unsigned j = 0; j < length; ++j)
                {
                    ordered_json connection;
                    connection["guid"] = GuidText(guids[j]);
                    connection["address"] = AddressText(addresses[j]);
                    connection["ping"] = graph.GetPingBetweenSystems(participants[i], guids[j]);
                    connections.push_back(connection);
                }
            }
            participant["connections"] = connections;
            participantArray.push_back(participant);
        }
        result["participants"] = participantArray;
        result["lowestAveragePingSystem"] = GuidText(graph.GetLowestAveragePingSystem());
    }

    void TopologyCommands::MeshInfo(ordered_json& result)
    {
        RakNet::FullyConnectedMesh2& mesh = RequireMesh();
        DataStructures::List<RakNet::RakNetGUID> participants;
        mesh.GetParticipantList(participants);
        DataStructures::List<RakNet::RakNetGUID> hostOrder;
        mesh.GetHostOrder(hostOrder);
        result["hostSystem"] = GuidText(mesh.GetHostSystem());
        result["isHostSystem"] = mesh.IsHostSystem();
        result["connectedHost"] = GuidText(mesh.GetConnectedHost());
        result["isConnectedHost"] = mesh.IsConnectedHost();
        result["connectedHostAddress"] = AddressText(mesh.GetConnectedHostAddr());
        result["participants"] = GuidArray(participants);
        result["hostOrder"] = GuidArray(hostOrder);
        result["totalConnectionCount"] = mesh.GetTotalConnectionCount();
    }
}
