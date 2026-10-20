#include "ReplicaManager3Commands.h"

#include "PluginCommands.h"

#include "BitStream.h"
#include "GetTime.h"
#include "MessageIdentifiers.h"
#include "NetworkIDManager.h"
#include "PluginInterface2.h"
#include "RakPeerInterface.h"
#include "RakString.h"
#include "ReplicaManager3.h"
#include "VariableDeltaSerializer.h"

#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <limits>

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

        bool Has(const json& command, const char* field)
        {
            auto it = command.find(field);
            return it != command.end() && !it->is_null();
        }

        int IntValue(const json& command, const char* field)
        {
            long long value = Required<long long>(command, field);
            if (value < std::numeric_limits<int>::min() || value > std::numeric_limits<int>::max())
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<int>(value);
        }

        std::uint64_t DecimalField(const json& command, const char* field)
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

        std::string Decimal(std::uint64_t value)
        {
            return std::to_string(value);
        }

        // RakString::Deserialize of what the sample prints, empty when nothing was written.
        std::string ReadText(RakNet::BitStream* bs)
        {
            if (bs->GetNumberOfBitsUsed() == 0)
                return "";
            RakNet::RakString text;
            bs->Read(text);
            return text.C_String();
        }

        using RakNet::Connection_RM3;
        using RakNet::Replica3;
        using RakNet::ReplicaManager3;

        // SampleReplica of Samples/ReplicaManager3/main.cpp, with the topology of the commands object instead of the
        // sample's global, the variables set by rm3Set instead of RandomizeVariables, and what the sample prints recorded.
        struct SampleReplica : public Replica3
        {
            explicit SampleReplica(ReplicaManager3Commands* commands) : commands_(commands)
            {
                var1Unreliable = 0;
                var2Unreliable = 0;
                var3Reliable = 0;
                var4Reliable = 0;
            }

            virtual RakNet::RakString GetName(void) const = 0;

            virtual void WriteAllocationID(RakNet::Connection_RM3*, RakNet::BitStream* allocationIdBitstream) const
            {
                allocationIdBitstream->Write(GetName());
            }

            virtual void SerializeConstruction(RakNet::BitStream* constructionBitstream, RakNet::Connection_RM3* destinationConnection)
            {
                variableDeltaSerializer.AddRemoteSystemVariableHistory(destinationConnection->GetRakNetGUID());
                constructionBitstream->Write(GetName() + RakNet::RakString(" SerializeConstruction"));
            }

            virtual bool DeserializeConstruction(RakNet::BitStream* constructionBitstream, RakNet::Connection_RM3*)
            {
                constructionText = ReadText(constructionBitstream);
                return true;
            }

            virtual void SerializeDestruction(RakNet::BitStream* destructionBitstream, RakNet::Connection_RM3* destinationConnection)
            {
                variableDeltaSerializer.RemoveRemoteSystemVariableHistory(destinationConnection->GetRakNetGUID());
                destructionBitstream->Write(GetName() + RakNet::RakString(" SerializeDestruction"));
            }

            virtual bool DeserializeDestruction(RakNet::BitStream* destructionBitstream, RakNet::Connection_RM3*)
            {
                ReplicaManager3Commands::Destruction destruction;
                destruction.networkId = GetNetworkID();
                destruction.kind = GetName().C_String();
                destruction.text = ReadText(destructionBitstream);
                destruction.deletingGuid = deletingSystemGUID.g;
                commands_->RecordDestruction(destruction);
                return true;
            }

            virtual void DeallocReplica(RakNet::Connection_RM3*)
            {
                delete this;
            }

            virtual void OnUserReplicaPreSerializeTick(void)
            {
                variableDeltaSerializer.OnPreSerializeTick();
            }

            virtual RakNet::RM3SerializationResult Serialize(RakNet::SerializeParameters* serializeParameters)
            {
                RakNet::VariableDeltaSerializer::SerializationContext serializationContext;

                serializeParameters->pro[0].reliability = UNRELIABLE_WITH_ACK_RECEIPT;
                serializeParameters->pro[0].sendReceipt = replicaManager->GetRakPeerInterface()->IncrementNextSendReceipt();
                serializeParameters->messageTimestamp = RakNet::GetTime();

                variableDeltaSerializer.BeginUnreliableAckedSerialize(
                    &serializationContext,
                    serializeParameters->destinationConnection->GetRakNetGUID(),
                    &serializeParameters->outputBitstream[0],
                    serializeParameters->pro[0].sendReceipt);
                variableDeltaSerializer.SerializeVariable(&serializationContext, var1Unreliable);
                variableDeltaSerializer.SerializeVariable(&serializationContext, var2Unreliable);
                variableDeltaSerializer.EndSerialize(&serializationContext);

                serializeParameters->pro[1].reliability = RELIABLE_ORDERED;

                variableDeltaSerializer.BeginIdenticalSerialize(
                    &serializationContext,
                    serializeParameters->whenLastSerialized == 0,
                    &serializeParameters->outputBitstream[1]);
                variableDeltaSerializer.SerializeVariable(&serializationContext, var3Reliable);
                variableDeltaSerializer.SerializeVariable(&serializationContext, var4Reliable);
                variableDeltaSerializer.EndSerialize(&serializationContext);

                return RakNet::RM3SR_SERIALIZED_ALWAYS;
            }

            virtual void Deserialize(RakNet::DeserializeParameters* deserializeParameters)
            {
                RakNet::VariableDeltaSerializer::DeserializationContext deserializationContext;

                variableDeltaSerializer.BeginDeserialize(&deserializationContext, &deserializeParameters->serializationBitstream[0]);
                variableDeltaSerializer.DeserializeVariable(&deserializationContext, var1Unreliable);
                variableDeltaSerializer.DeserializeVariable(&deserializationContext, var2Unreliable);
                variableDeltaSerializer.EndDeserialize(&deserializationContext);

                variableDeltaSerializer.BeginDeserialize(&deserializationContext, &deserializeParameters->serializationBitstream[1]);
                variableDeltaSerializer.DeserializeVariable(&deserializationContext, var3Reliable);
                variableDeltaSerializer.DeserializeVariable(&deserializationContext, var4Reliable);
                variableDeltaSerializer.EndDeserialize(&deserializationContext);
            }

            virtual void OnPoppedConnection(RakNet::Connection_RM3* droppedConnection)
            {
                variableDeltaSerializer.RemoveRemoteSystemVariableHistory(droppedConnection->GetRakNetGUID());
            }

            void NotifyReplicaOfMessageDeliveryStatus(RakNet::RakNetGUID guid, std::uint32_t receiptId, bool messageArrived)
            {
                variableDeltaSerializer.OnMessageReceipt(guid, receiptId, messageArrived);
            }

            bool IsClient() const { return commands_->GetTopology() == ReplicaManager3Commands::Topology::Client; }

            int var1Unreliable, var2Unreliable, var3Reliable, var4Reliable;
            std::string constructionText;
            RakNet::VariableDeltaSerializer variableDeltaSerializer;

        private:
            ReplicaManager3Commands* commands_;
        };

        struct ClientCreatible_ClientSerialized : public SampleReplica
        {
            using SampleReplica::SampleReplica;
            virtual RakNet::RakString GetName(void) const { return RakNet::RakString("ClientCreatible_ClientSerialized"); }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_ClientConstruction(destinationConnection, !IsClient()); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_ClientConstruction(sourceConnection, !IsClient()); }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_ClientSerializable(destinationConnection, !IsClient()); }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const { return QueryActionOnPopConnection_Client(droppedConnection); }
        };

        struct ServerCreated_ClientSerialized : public SampleReplica
        {
            using SampleReplica::SampleReplica;
            virtual RakNet::RakString GetName(void) const { return RakNet::RakString("ServerCreated_ClientSerialized"); }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_ServerConstruction(destinationConnection, !IsClient()); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_ServerConstruction(sourceConnection, !IsClient()); }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_ClientSerializable(destinationConnection, !IsClient()); }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const { return QueryActionOnPopConnection_Server(droppedConnection); }
        };

        struct ClientCreatible_ServerSerialized : public SampleReplica
        {
            using SampleReplica::SampleReplica;
            virtual RakNet::RakString GetName(void) const { return RakNet::RakString("ClientCreatible_ServerSerialized"); }
            virtual RakNet::RM3SerializationResult Serialize(RakNet::SerializeParameters* serializeParameters)
            {
                if (IsClient())
                    return RakNet::RM3SR_DO_NOT_SERIALIZE;
                return SampleReplica::Serialize(serializeParameters);
            }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_ClientConstruction(destinationConnection, !IsClient()); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_ClientConstruction(sourceConnection, !IsClient()); }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_ServerSerializable(destinationConnection, !IsClient()); }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const { return QueryActionOnPopConnection_Client(droppedConnection); }
        };

        struct ServerCreated_ServerSerialized : public SampleReplica
        {
            using SampleReplica::SampleReplica;
            virtual RakNet::RakString GetName(void) const { return RakNet::RakString("ServerCreated_ServerSerialized"); }
            virtual RakNet::RM3SerializationResult Serialize(RakNet::SerializeParameters* serializeParameters)
            {
                if (IsClient())
                    return RakNet::RM3SR_DO_NOT_SERIALIZE;
                return SampleReplica::Serialize(serializeParameters);
            }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_ServerConstruction(destinationConnection, !IsClient()); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_ServerConstruction(sourceConnection, !IsClient()); }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_ServerSerializable(destinationConnection, !IsClient()); }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const { return QueryActionOnPopConnection_Server(droppedConnection); }
        };

        struct P2PReplica : public SampleReplica
        {
            using SampleReplica::SampleReplica;
            virtual RakNet::RakString GetName(void) const { return RakNet::RakString("P2PReplica"); }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_PeerToPeer(destinationConnection); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_PeerToPeer(sourceConnection); }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_PeerToPeer(destinationConnection); }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const { return QueryActionOnPopConnection_PeerToPeer(droppedConnection); }
        };

        // User of Samples/ReplicatedLogin/ReplicatedLogin.cpp, with runningAsServer from the topology.
        class User : public Replica3
        {
        public:
            explicit User(ReplicaManager3Commands* commands) : commands_(commands) { score = 0; }

            virtual void WriteAllocationID(RakNet::Connection_RM3*, RakNet::BitStream* allocationIdBitstream) const { allocationIdBitstream->Write("User"); }
            virtual RakNet::RM3ConstructionState QueryConstruction(RakNet::Connection_RM3* destinationConnection, ReplicaManager3*) { return QueryConstruction_ServerConstruction(destinationConnection, RunningAsServer()); }
            virtual bool QueryRemoteConstruction(RakNet::Connection_RM3* sourceConnection) { return QueryRemoteConstruction_ServerConstruction(sourceConnection, RunningAsServer()); }

            static void SerializeToBitStream(User* user, RakNet::BitStream* bitStream)
            {
                bitStream->Write(user->score);
                bitStream->Write(user->username);
                bitStream->Write(user->guid);
            }

            static void DeserializeToBitStream(User* user, RakNet::BitStream* bitStream)
            {
                bitStream->Read(user->score);
                bitStream->Read(user->username);
                bitStream->Read(user->guid);
            }

            virtual void SerializeConstruction(RakNet::BitStream* constructionBitstream, RakNet::Connection_RM3*) { SerializeToBitStream(this, constructionBitstream); }
            virtual bool DeserializeConstruction(RakNet::BitStream* constructionBitstream, RakNet::Connection_RM3*)
            {
                DeserializeToBitStream(this, constructionBitstream);
                return true;
            }
            virtual void SerializeDestruction(RakNet::BitStream*, RakNet::Connection_RM3*) {}
            virtual bool DeserializeDestruction(RakNet::BitStream*, RakNet::Connection_RM3*)
            {
                ReplicaManager3Commands::Destruction destruction;
                destruction.networkId = GetNetworkID();
                destruction.kind = "User";
                destruction.deletingGuid = deletingSystemGUID.g;
                commands_->RecordDestruction(destruction);
                return true;
            }
            virtual RakNet::RM3ActionOnPopConnection QueryActionOnPopConnection(RakNet::Connection_RM3* droppedConnection) const
            {
                if (RunningAsServer())
                    return QueryActionOnPopConnection_Server(droppedConnection);
                return QueryActionOnPopConnection_Client(droppedConnection);
            }
            virtual void DeallocReplica(RakNet::Connection_RM3*) { delete this; }
            virtual RakNet::RM3QuerySerializationResult QuerySerialization(RakNet::Connection_RM3* destinationConnection) { return QuerySerialization_ServerSerializable(destinationConnection, RunningAsServer()); }
            virtual RakNet::RM3SerializationResult Serialize(RakNet::SerializeParameters* serializeParameters)
            {
                SerializeToBitStream(this, &serializeParameters->outputBitstream[0]);
                return RakNet::RM3SR_BROADCAST_IDENTICALLY;
            }
            virtual void Deserialize(RakNet::DeserializeParameters* deserializeParameters)
            {
                if (deserializeParameters->bitstreamWrittenTo[0])
                    DeserializeToBitStream(this, &deserializeParameters->serializationBitstream[0]);
            }

            int score;
            RakNet::RakString username;
            RakNet::RakNetGUID guid;

        private:
            bool RunningAsServer() const { return commands_->GetTopology() == ReplicaManager3Commands::Topology::Server; }

            ReplicaManager3Commands* commands_;
        };

        Replica3* NewReplica(const std::string& kind, ReplicaManager3Commands* commands)
        {
            if (kind == "ClientCreatible_ClientSerialized")
                return new ClientCreatible_ClientSerialized(commands);
            if (kind == "ServerCreated_ClientSerialized")
                return new ServerCreated_ClientSerialized(commands);
            if (kind == "ClientCreatible_ServerSerialized")
                return new ClientCreatible_ServerSerialized(commands);
            if (kind == "ServerCreated_ServerSerialized")
                return new ServerCreated_ServerSerialized(commands);
            if (kind == "P2PReplica")
                return new P2PReplica(commands);
            if (kind == "User")
                return new User(commands);
            return nullptr;
        }

        // SampleConnection of the sample, which groups download messages, and SampleConnectionRM3 of ReplicatedLogin,
        // which does not.
        class SampleConnection : public Connection_RM3
        {
        public:
            SampleConnection(const RakNet::SystemAddress& systemAddress, RakNet::RakNetGUID guid, ReplicaManager3Commands* commands, bool groupDownloads)
                : Connection_RM3(systemAddress, guid), commands_(commands), groupDownloads_(groupDownloads)
            {
            }

            virtual bool QueryGroupDownloadMessages(void) const { return groupDownloads_; }

            virtual Replica3* AllocReplica(RakNet::BitStream* allocationId, ReplicaManager3*)
            {
                RakNet::RakString typeName;
                allocationId->Read(typeName);
                return NewReplica(typeName.C_String(), commands_);
            }

        private:
            ReplicaManager3Commands* commands_;
            bool groupDownloads_;
        };
    }

    class ReplicaManager3Commands::Manager : public ReplicaManager3
    {
    public:
        Manager(ReplicaManager3Commands* commands, bool groupDownloads) : commands_(commands), groupDownloads_(groupDownloads) {}

        virtual Connection_RM3* AllocConnection(const RakNet::SystemAddress& systemAddress, RakNet::RakNetGUID rakNetGUID) const
        {
            return new SampleConnection(systemAddress, rakNetGUID, commands_, groupDownloads_);
        }

        virtual void DeallocConnection(Connection_RM3* connection) const
        {
            delete connection;
        }

    private:
        ReplicaManager3Commands* commands_;
        bool groupDownloads_;
    };

    // The sample's handling of ID_SND_RECEIPT_ACKED and ID_SND_RECEIPT_LOSS, as a plugin that passes them on.
    class ReplicaManager3Commands::ReceiptForwarder : public RakNet::PluginInterface2
    {
    public:
        explicit ReceiptForwarder(ReplicaManager3* manager) : manager_(manager) {}

        virtual RakNet::PluginReceiveResult OnReceive(RakNet::Packet* packet)
        {
            if ((packet->data[0] == ID_SND_RECEIPT_ACKED || packet->data[0] == ID_SND_RECEIPT_LOSS) && packet->length >= 5)
            {
                std::uint32_t msgNumber;
                std::memcpy(&msgNumber, packet->data + 1, 4);
                DataStructures::List<Replica3*> replicaListOut;
                manager_->GetReplicasCreatedByMe(replicaListOut);
                for (unsigned int idx = 0; idx < replicaListOut.Size(); idx++)
                {
                    if (SampleReplica* sample = dynamic_cast<SampleReplica*>(replicaListOut[idx]))
                        sample->NotifyReplicaOfMessageDeliveryStatus(packet->guid, msgNumber, packet->data[0] == ID_SND_RECEIPT_ACKED);
                }
            }
            return RakNet::RR_CONTINUE_PROCESSING;
        }

    private:
        ReplicaManager3* manager_;
    };

    ReplicaManager3Commands::ReplicaManager3Commands(RakNet::RakPeerInterface* peer) : peer_(peer)
    {
    }

    ReplicaManager3Commands::~ReplicaManager3Commands()
    {
        // The peer is destroyed first, and its shutdown popped every connection; the manager then clears its replicas'
        // IDs, and the replicas themselves go with the process.
        receipts_.reset();
        manager_.reset();
        networkIdManager_.reset();
    }

    ReplicaManager3Commands::Manager& ReplicaManager3Commands::RequireManager()
    {
        if (!manager_)
            throw PluginCommandError("no ReplicaManager3 is attached; send rm3Attach first");
        return *manager_;
    }

    bool ReplicaManager3Commands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd.rfind("rm3", 0) != 0)
            return false;

        if (cmd == "rm3Attach")
        {
            if (manager_)
                return true;
            std::string topology = Optional<std::string>(command, "topology", "SERVER");
            if (topology == "CLIENT")
                topology_ = Topology::Client;
            else if (topology == "SERVER")
                topology_ = Topology::Server;
            else if (topology == "P2P")
                topology_ = Topology::P2P;
            else
                throw PluginCommandError("unknown topology '" + topology + "'");
            long long interval = Optional<long long>(command, "autoSerializeMs", 30);
            networkIdManager_.reset(new RakNet::NetworkIDManager());
            manager_.reset(new Manager(this, Optional<bool>(command, "groupDownloads", true)));
            receipts_.reset(new ReceiptForwarder(manager_.get()));
            peer_->AttachPlugin(manager_.get());
            peer_->AttachPlugin(receipts_.get());
            manager_->SetNetworkIDManager(networkIdManager_.get());
            // Negative is never: RakNet::Time is unsigned, so -1 is its largest value.
            manager_->SetAutoSerializeInterval(static_cast<RakNet::Time>(interval));
            return true;
        }

        Manager& manager = RequireManager();
        if (cmd == "rm3Create")
        {
            // Every kind is referenced in this one call, as the sample's 'C' does, so they are constructed in one update.
            const json kinds = Required<json>(command, "kinds");
            if (!kinds.is_array() || kinds.empty())
                throw PluginCommandError("field 'kinds' must be a non-empty array");
            std::vector<Replica3*> created;
            for (const json& kindField : kinds)
            {
                std::string kind = kindField.get<std::string>();
                Replica3* replica = NewReplica(kind, this);
                if (replica == nullptr)
                {
                    for (Replica3* made : created)
                        delete made;
                    throw PluginCommandError("unknown kind '" + kind + "'");
                }
                if (User* user = dynamic_cast<User*>(replica))
                {
                    user->username = Optional<std::string>(command, "username", "").c_str();
                    if (Has(command, "userGuid"))
                        user->guid = RakNet::RakNetGUID(DecimalField(command, "userGuid"));
                }
                created.push_back(replica);
            }
            ordered_json ids = ordered_json::array();
            for (Replica3* replica : created)
            {
                manager.Reference(replica);
                ids.push_back(Decimal(replica->GetNetworkID()));
            }
            result["networkIds"] = ids;
        }
        else if (cmd == "rm3Set")
        {
            Replica3* replica = networkIdManager_->GET_OBJECT_FROM_ID<Replica3*>(DecimalField(command, "networkId"));
            if (replica == nullptr)
                throw PluginCommandError("no replica has that networkId");
            if (SampleReplica* sample = dynamic_cast<SampleReplica*>(replica))
            {
                if (Has(command, "var1"))
                    sample->var1Unreliable = IntValue(command, "var1");
                if (Has(command, "var2"))
                    sample->var2Unreliable = IntValue(command, "var2");
                if (Has(command, "var3"))
                    sample->var3Reliable = IntValue(command, "var3");
                if (Has(command, "var4"))
                    sample->var4Reliable = IntValue(command, "var4");
            }
            else if (User* user = dynamic_cast<User*>(replica))
            {
                if (Has(command, "score"))
                    user->score = IntValue(command, "score");
                if (Has(command, "username"))
                    user->username = Required<std::string>(command, "username").c_str();
            }
        }
        else if (cmd == "rm3DestroyMine")
        {
            // The sample's 'D'.
            DataStructures::List<Replica3*> replicaListOut;
            manager.GetReplicasCreatedByMe(replicaListOut);
            manager.BroadcastDestructionList(replicaListOut, RakNet::UNASSIGNED_SYSTEM_ADDRESS);
            for (unsigned int i = 0; i < replicaListOut.Size(); i++)
                RakNet::OP_DELETE(replicaListOut[i], _FILE_AND_LINE_);
            result["destroyed"] = replicaListOut.Size();
        }
        else if (cmd == "rm3Destroy")
        {
            // ReplicatedLogin's DeleteUserWithGuid.
            Replica3* replica = networkIdManager_->GET_OBJECT_FROM_ID<Replica3*>(DecimalField(command, "networkId"));
            if (replica == nullptr)
                throw PluginCommandError("no replica has that networkId");
            replica->BroadcastDestruction();
            manager.Dereference(replica);
            delete replica;
        }
        else if (cmd == "rm3Info")
        {
            ordered_json replicas = ordered_json::array();
            DataStructures::List<Replica3*> referenced;
            manager.GetReferencedReplicaList(referenced);
            for (unsigned int i = 0; i < referenced.Size(); i++)
            {
                Replica3* replica = referenced[i];
                ordered_json item;
                item["networkId"] = Decimal(replica->GetNetworkID());
                item["creatingGuid"] = Decimal(replica->creatingSystemGUID.g);
                if (SampleReplica* sample = dynamic_cast<SampleReplica*>(replica))
                {
                    item["kind"] = sample->GetName().C_String();
                    item["var1"] = sample->var1Unreliable;
                    item["var2"] = sample->var2Unreliable;
                    item["var3"] = sample->var3Reliable;
                    item["var4"] = sample->var4Reliable;
                    item["constructionText"] = sample->constructionText;
                }
                else if (User* user = dynamic_cast<User*>(replica))
                {
                    item["kind"] = "User";
                    item["score"] = user->score;
                    item["username"] = user->username.C_String();
                    item["userGuid"] = Decimal(user->guid.g);
                }
                replicas.push_back(item);
            }
            ordered_json connections = ordered_json::array();
            for (unsigned int i = 0; i < manager.GetConnectionCount(); i++)
            {
                Connection_RM3* connection = manager.GetConnectionAtIndex(i);
                DataStructures::List<Replica3*> constructed;
                connection->GetConstructedReplicas(constructed);
                ordered_json ids = ordered_json::array();
                for (unsigned int j = 0; j < constructed.Size(); j++)
                    ids.push_back(Decimal(constructed[j]->GetNetworkID()));
                ordered_json item;
                item["guid"] = Decimal(connection->GetRakNetGUID().g);
                item["validated"] = connection->isValidated;
                item["downloadComplete"] = connection->GetDownloadWasCompleted();
                item["constructed"] = ids;
                connections.push_back(item);
            }
            ordered_json destructions = ordered_json::array();
            for (const Destruction& destruction : destructions_)
            {
                ordered_json item;
                item["networkId"] = Decimal(destruction.networkId);
                item["kind"] = destruction.kind;
                item["text"] = destruction.text;
                item["deletingGuid"] = Decimal(destruction.deletingGuid);
                destructions.push_back(item);
            }
            result["replicas"] = replicas;
            result["connections"] = connections;
            result["destructions"] = destructions;
            result["allDownloadsCompleted"] = manager.GetAllConnectionDownloadsCompleted();
        }
        else
        {
            return false;
        }
        return true;
    }
}
