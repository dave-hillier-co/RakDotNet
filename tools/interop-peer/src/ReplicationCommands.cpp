#include "ReplicationCommands.h"

#include "Hex.h"
#include "Names.h"
#include "PluginCommands.h"

#include "BitStream.h"
#include "GridSectorizer.h"
#include "MessageIdentifiers.h"
#include "NetworkIDManager.h"
#include "NetworkIDObject.h"
#include "RPC4Plugin.h"
#include "RakPeerInterface.h"
#include "RandSync.h"
#include "Rand.h"
#include "VariableDeltaSerializer.h"

#include <array>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <utility>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    namespace
    {
        constexpr std::size_t kMaxHandlers = 32;

        // RPC4 takes plain function pointers, so each registration gets a function of its own that knows its index.
        ReplicationCommands* g_commands = nullptr;

        using FunctionPointer = void (*)(RakNet::BitStream*, RakNet::Packet*);
        using BlockingPointer = void (*)(RakNet::BitStream*, RakNet::BitStream*, RakNet::Packet*);

        template <std::size_t N>
        void Function(RakNet::BitStream* parameters, RakNet::Packet* packet)
        {
            g_commands->RecordInvocation(static_cast<int>(N), parameters, packet);
        }

        template <std::size_t N>
        void Blocking(RakNet::BitStream* parameters, RakNet::BitStream* returnData, RakNet::Packet* packet)
        {
            g_commands->RecordBlockingInvocation(static_cast<int>(N), parameters, returnData, packet);
        }

        template <std::size_t... I>
        constexpr std::array<FunctionPointer, sizeof...(I)> MakeFunctions(std::index_sequence<I...>)
        {
            return {{&Function<I>...}};
        }

        template <std::size_t... I>
        constexpr std::array<BlockingPointer, sizeof...(I)> MakeBlocking(std::index_sequence<I...>)
        {
            return {{&Blocking<I>...}};
        }

        const std::array<FunctionPointer, kMaxHandlers> kFunctions = MakeFunctions(std::make_index_sequence<kMaxHandlers>());
        const std::array<BlockingPointer, kMaxHandlers> kBlocking = MakeBlocking(std::make_index_sequence<kMaxHandlers>());

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

        long long IntField(const json& command, const char* field, long long minimum, long long maximum, long long fallback, bool required)
        {
            long long value = required ? Required<long long>(command, field) : Optional<long long>(command, field, fallback);
            if (value < minimum || value > maximum)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return value;
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

        RakNet::RakNetGUID GuidField(const json& command, const char* field)
        {
            return RakNet::RakNetGUID(DecimalField(command, field));
        }

        std::vector<unsigned char> HexField(const json& command, const char* field)
        {
            std::vector<unsigned char> bytes;
            if (!TryFromHex(Required<std::string>(command, field), bytes))
                throw PluginCommandError(std::string("field '") + field + "' is not valid hex");
            return bytes;
        }

        // A hex field and an optional bit length ("bits", default every bit of the bytes), MSB first.
        unsigned BitsField(const json& command, const char* field, const std::vector<unsigned char>& bytes)
        {
            long long bits = IntField(command, field, 0, static_cast<long long>(bytes.size()) * 8, static_cast<long long>(bytes.size()) * 8, false);
            if (bits <= static_cast<long long>(bytes.size()) * 8 - 8)
                throw PluginCommandError(std::string("field '") + field + "' leaves whole bytes unused");
            return static_cast<unsigned>(bits);
        }

        void WriteHexBits(RakNet::BitStream& stream, const std::vector<unsigned char>& bytes, unsigned bits)
        {
            if (bits > 0)
                stream.WriteBits(bytes.data(), bits, false);
        }

        float FloatField(const json& command, const char* field)
        {
            return static_cast<float>(Required<double>(command, field));
        }

        std::string HexOf(const RakNet::BitStream& stream)
        {
            return ToHex(stream.GetData(), stream.GetNumberOfBytesUsed());
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                return "";
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        struct SendFields
        {
            PacketReliability reliability;
            PacketPriority priority;
            char channel;
        };

        SendFields ParseSendFields(const json& command, PacketReliability defaultReliability)
        {
            SendFields fields;
            fields.reliability = defaultReliability;
            if (Has(command, "reliability"))
            {
                std::string name = Required<std::string>(command, "reliability");
                if (!TryParseReliability(name, fields.reliability))
                    throw PluginCommandError("unknown reliability '" + name + "'");
            }
            std::string priorityName = Optional<std::string>(command, "priority", "HIGH_PRIORITY");
            if (!TryParsePriority(priorityName, fields.priority))
                throw PluginCommandError("unknown priority '" + priorityName + "'");
            fields.channel = static_cast<char>(IntField(command, "channel", 0, 31, 0, false));
            return fields;
        }
    }

    // A NetworkIDObject the driver names by handle.
    class ReplicationCommands::Object : public RakNet::NetworkIDObject
    {
    public:
        explicit Object(int handle) : handle_(handle) {}

        int Handle() const { return handle_; }

    private:
        int handle_;
    };

    ReplicationCommands::ReplicationCommands(RakNet::RakPeerInterface* peer) : peer_(peer), replicaManager3_(peer)
    {
        g_commands = this;
    }

    ReplicationCommands::~ReplicationCommands()
    {
        // The peer is destroyed first; it never frees plugins.
        if (rpc4_ != nullptr)
            RakNet::RPC4::DestroyInstance(rpc4_);
        objects_.clear();
        if (g_commands == this)
            g_commands = nullptr;
    }

    bool ReplicationCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        return DispatchRandSync(cmd, command, result)
            || DispatchNetworkId(cmd, command, result)
            || DispatchGrid(cmd, command, result)
            || DispatchDelta(cmd, command, result)
            || DispatchRpc4(cmd, command, result)
            || replicaManager3_.Dispatch(cmd, command, result);
    }

    bool ReplicationCommands::DispatchRandSync(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "rakNetRandom")
        {
            RakNet::RakNetRandom random;
            random.SeedMT(static_cast<unsigned>(IntField(command, "seed", 0, std::numeric_limits<std::uint32_t>::max(), 0, true)));
            long long count = IntField(command, "count", 0, 100000, 0, false);
            ordered_json values = ordered_json::array();
            ordered_json floats = ordered_json::array();
            for (long long i = 0; i < count; ++i)
                values.push_back(random.RandomMT());
            long long floatCount = IntField(command, "floatCount", 0, 100000, 0, false);
            for (long long i = 0; i < floatCount; ++i)
            {
                float value = random.FrandomMT();
                std::uint32_t bits;
                std::memcpy(&bits, &value, sizeof(bits));
                floats.push_back(bits);
            }
            result["values"] = values;
            result["floatBits"] = floats;
            if (Has(command, "fillBytes"))
            {
                std::vector<unsigned char> buffer(static_cast<std::size_t>(IntField(command, "fillBytes", 0, 4096, 0, true)));
                random.FillBufferMT(buffer.data(), static_cast<unsigned>(buffer.size()));
                result["fill"] = ToHex(buffer.data(), buffer.size());
                result["afterFill"] = random.RandomMT();
            }
            return true;
        }

        if (cmd.rfind("randSync", 0) != 0)
            return false;
        if (!randSync_)
            randSync_.reset(new RakNet::RakNetRandomSync());
        RakNet::RakNetRandomSync& sync = *randSync_;

        if (cmd == "randSyncSeed")
        {
            unsigned seed = static_cast<unsigned>(IntField(command, "seed", 0, std::numeric_limits<std::uint32_t>::max(), 0, true));
            if (Has(command, "skip"))
                sync.SeedMT(seed, static_cast<std::uint32_t>(IntField(command, "skip", 0, std::numeric_limits<std::uint32_t>::max(), 0, true)));
            else
                sync.SeedMT(seed);
        }
        else if (cmd == "randSyncNext")
        {
            long long count = IntField(command, "count", 0, 100000, 1, false);
            bool asFloat = Optional<bool>(command, "float", false);
            ordered_json values = ordered_json::array();
            for (long long i = 0; i < count; ++i)
            {
                if (asFloat)
                {
                    float value = sync.FrandomMT();
                    std::uint32_t bits;
                    std::memcpy(&bits, &value, sizeof(bits));
                    values.push_back(bits);
                }
                else
                {
                    values.push_back(sync.RandomMT());
                }
            }
            result["values"] = values;
        }
        else if (cmd == "randSyncSerializeConstruction")
        {
            RakNet::BitStream stream;
            sync.SerializeConstruction(&stream);
            result["data"] = HexOf(stream);
        }
        else if (cmd == "randSyncDeserializeConstruction")
        {
            std::vector<unsigned char> bytes = HexField(command, "data");
            RakNet::BitStream stream(bytes.data(), static_cast<unsigned>(bytes.size()), false);
            result["result"] = sync.DeserializeConstruction(&stream);
        }
        else if (cmd == "randSyncSerialize")
        {
            RakNet::BitStream stream;
            sync.Serialize(&stream);
            result["data"] = HexOf(stream);
        }
        else if (cmd == "randSyncDeserialize")
        {
            std::vector<unsigned char> bytes = HexField(command, "data");
            if (bytes.size() < 4)
                throw PluginCommandError("field 'data' is shorter than a call count");
            RakNet::BitStream stream(bytes.data(), static_cast<unsigned>(bytes.size()), false);
            sync.Deserialize(&stream);
        }
        else if (cmd == "randSyncSetCallCount")
        {
            sync.SetCallCount(static_cast<std::uint32_t>(IntField(command, "callCount", 0, std::numeric_limits<std::uint32_t>::max(), 0, true)));
        }
        else if (cmd == "randSyncInfo")
        {
            result["seed"] = sync.GetSeed();
            result["callCount"] = sync.GetCallCount();
        }
        else
        {
            return false;
        }
        return true;
    }

    bool ReplicationCommands::DispatchNetworkId(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd.rfind("networkId", 0) != 0)
            return false;
        if (!networkIdManager_)
            networkIdManager_.reset(new RakNet::NetworkIDManager());

        auto requireObject = [this, &command](const char* field) -> Object& {
            int handle = static_cast<int>(IntField(command, field, 0, std::numeric_limits<int>::max(), 0, true));
            auto it = objects_.find(handle);
            if (it == objects_.end())
                throw PluginCommandError("no object with handle " + std::to_string(handle));
            return *it->second;
        };

        if (cmd == "networkIdObjectNew")
        {
            int handle = static_cast<int>(IntField(command, "handle", 0, std::numeric_limits<int>::max(), 0, true));
            if (objects_.count(handle) != 0)
                throw PluginCommandError("an object with handle " + std::to_string(handle) + " exists");
            objects_[handle].reset(new Object(handle));
        }
        else if (cmd == "networkIdObjectSetManager")
        {
            requireObject("handle").SetNetworkIDManager(Optional<bool>(command, "attach", true) ? networkIdManager_.get() : nullptr);
        }
        else if (cmd == "networkIdObjectSetId")
        {
            Object& object = requireObject("handle");
            object.SetNetworkID(DecimalField(command, "networkId"));
        }
        else if (cmd == "networkIdObjectSetParent")
        {
            Object& object = requireObject("handle");
            object.SetParent(Has(command, "parent") ? &requireObject("parent") : nullptr);
        }
        else if (cmd == "networkIdObjectDelete")
        {
            int handle = requireObject("handle").Handle();
            objects_.erase(handle);
        }
        else if (cmd == "networkIdObjectInfo")
        {
            Object& object = requireObject("handle");
            result["networkId"] = std::to_string(object.GetNetworkID());
            result["hasManager"] = object.GetNetworkIDManager() != nullptr;
        }
        else if (cmd == "networkIdLookup")
        {
            RakNet::NetworkID id = DecimalField(command, "networkId");
            Object* base = static_cast<Object*>(networkIdManager_->GET_BASE_OBJECT_FROM_ID(id));
            Object* object = networkIdManager_->GET_OBJECT_FROM_ID<Object*>(id);
            result["handle"] = base != nullptr ? json(base->Handle()) : json(nullptr);
            result["object"] = object != nullptr ? json(object->Handle()) : json(nullptr);
        }
        else if (cmd == "networkIdClear")
        {
            networkIdManager_->Clear();
        }
        else
        {
            return false;
        }
        return true;
    }

    bool ReplicationCommands::DispatchGrid(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "gridInit")
        {
            float maxCellWidth = FloatField(command, "maxCellWidth");
            float maxCellHeight = FloatField(command, "maxCellHeight");
            if (!(maxCellWidth > 0.0f) || !(maxCellHeight > 0.0f))
                throw PluginCommandError("the cell size must be positive");
            if (!grid_)
                grid_.reset(new GridSectorizer());
            grid_->Init(maxCellWidth, maxCellHeight, FloatField(command, "minX"), FloatField(command, "minY"), FloatField(command, "maxX"), FloatField(command, "maxY"));
        }
        else if (cmd == "gridAdd")
        {
            long long entry = IntField(command, "entry", 1, std::numeric_limits<int>::max(), 0, true);
            float minX = FloatField(command, "minX");
            float minY = FloatField(command, "minY");
            float maxX = FloatField(command, "maxX");
            float maxY = FloatField(command, "maxY");
            if (!(minX < maxX && minY < maxY))
                throw PluginCommandError("the rectangle is empty");
            RequireGrid().AddEntry(reinterpret_cast<void*>(static_cast<std::intptr_t>(entry)), minX, minY, maxX, maxY);
        }
        else if (cmd == "gridGet")
        {
            DataStructures::List<void*> entries;
            RequireGrid().GetEntries(entries, FloatField(command, "minX"), FloatField(command, "minY"), FloatField(command, "maxX"), FloatField(command, "maxY"));
            ordered_json array = ordered_json::array();
            for (unsigned i = 0; i < entries.Size(); ++i)
                array.push_back(static_cast<long long>(reinterpret_cast<std::intptr_t>(entries[i])));
            result["entries"] = array;
        }
        else if (cmd == "gridClear")
        {
            RequireGrid().Clear();
        }
        else
        {
            return false;
        }
        return true;
    }

    GridSectorizer& ReplicationCommands::RequireGrid()
    {
        if (!grid_)
            throw PluginCommandError("no GridSectorizer is initialised; send gridInit first");
        return *grid_;
    }

    bool ReplicationCommands::DispatchDelta(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd.rfind("delta", 0) != 0)
            return false;
        if (!deltaSerializer_)
            deltaSerializer_.reset(new RakNet::VariableDeltaSerializer());

        if (cmd == "deltaSerialize")
        {
            DeltaSerialize(command, result);
        }
        else if (cmd == "deltaPreSerializeTick")
        {
            deltaSerializer_->OnPreSerializeTick();
        }
        else if (cmd == "deltaReceipt")
        {
            deltaSerializer_->OnMessageReceipt(
                GuidField(command, "target"),
                static_cast<std::uint32_t>(IntField(command, "receipt", 0, std::numeric_limits<std::uint32_t>::max(), 0, true)),
                Required<bool>(command, "arrived"));
        }
        else if (cmd == "deltaRemoveSystem")
        {
            deltaSerializer_->RemoveRemoteSystemVariableHistory(GuidField(command, "target"));
        }
        else if (cmd == "deltaDeserialize")
        {
            std::vector<unsigned char> bytes = HexField(command, "data");
            long long count = IntField(command, "count", 0, 448, 0, true);
            RakNet::BitStream stream(bytes.data(), static_cast<unsigned>(bytes.size()), false);
            RakNet::VariableDeltaSerializer::DeserializationContext context;
            deltaSerializer_->BeginDeserialize(&context, &stream);
            ordered_json variables = ordered_json::array();
            for (long long i = 0; i < count; ++i)
            {
                int value = 0;
                bool changed = deltaSerializer_->DeserializeVariable(&context, value);
                ordered_json item;
                item["changed"] = changed;
                item["value"] = changed ? json(value) : json(nullptr);
                variables.push_back(item);
            }
            deltaSerializer_->EndDeserialize(&context);
            result["variables"] = variables;
        }
        else
        {
            return false;
        }
        return true;
    }

    // Each group is one Begin*Serialize / SerializeVariable... / EndSerialize run, all on one SerializationContext as
    // the ReplicaManager3 sample's Serialize uses one; a group with sendTo is sent as its messageId then its bits.
    void ReplicationCommands::DeltaSerialize(const json& command, ordered_json& result)
    {
        RakNet::VariableDeltaSerializer& serializer = *deltaSerializer_;
        if (Optional<bool>(command, "preSerializeTick", false))
            serializer.OnPreSerializeTick();

        RakNet::VariableDeltaSerializer::SerializationContext context;
        ordered_json groups = ordered_json::array();
        for (const json& group : Required<json>(command, "groups"))
        {
            std::string mode = Required<std::string>(group, "mode");
            bool sending = Has(group, "sendTo");
            RakNet::BitStream stream;
            std::uint32_t receipt = 0;
            if (mode == "UNRELIABLE_ACKED")
            {
                receipt = sending
                    ? peer_->IncrementNextSendReceipt()
                    : static_cast<std::uint32_t>(IntField(group, "receipt", 0, std::numeric_limits<std::uint32_t>::max(), 0, true));
                serializer.BeginUnreliableAckedSerialize(&context, GuidField(group, "target"), &stream, receipt);
            }
            else if (mode == "UNIQUE")
            {
                serializer.BeginUniqueSerialize(&context, GuidField(group, "target"), &stream);
            }
            else if (mode == "IDENTICAL")
            {
                serializer.BeginIdenticalSerialize(&context, Optional<bool>(group, "first", false), &stream);
            }
            else
            {
                throw PluginCommandError("unknown mode '" + mode + "'");
            }
            for (const json& value : Required<json>(group, "values"))
                serializer.SerializeVariable(&context, value.get<int>());
            serializer.EndSerialize(&context);

            ordered_json item;
            item["data"] = HexOf(stream);
            item["bits"] = stream.GetNumberOfBitsUsed();
            item["receipt"] = receipt;
            bool sent = false;
            if (sending && stream.GetNumberOfBitsUsed() > 0)
            {
                SendFields fields = ParseSendFields(group, mode == "UNRELIABLE_ACKED" ? UNRELIABLE_WITH_ACK_RECEIPT : RELIABLE_ORDERED);
                RakNet::BitStream out;
                out.Write(static_cast<RakNet::MessageID>(IntField(group, "messageId", 0, 255, ID_USER_PACKET_ENUM, false)));
                stream.ResetReadPointer();
                out.Write(&stream);
                peer_->Send(&out, fields.priority, fields.reliability, fields.channel, GuidField(group, "sendTo"), false, receipt);
                sent = true;
            }
            item["sent"] = sent;
            groups.push_back(item);
        }
        result["groups"] = groups;
    }

    bool ReplicationCommands::DispatchRpc4(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd.rfind("rpc4", 0) != 0)
            return false;

        if (cmd == "rpc4Attach")
        {
            if (rpc4_ == nullptr)
            {
                rpc4_ = RakNet::RPC4::GetInstance();
                peer_->AttachPlugin(rpc4_);
            }
            return true;
        }

        RakNet::RPC4& rpc = RequireRpc4();
        auto optionalParameters = [&command](RakNet::BitStream& stream) -> RakNet::BitStream* {
            if (!Has(command, "parameters"))
                return nullptr;
            std::vector<unsigned char> bytes = HexField(command, "parameters");
            WriteHexBits(stream, bytes, BitsField(command, "bits", bytes));
            return &stream;
        };

        if (cmd == "rpc4RegisterFunction")
        {
            std::string name = Required<std::string>(command, "name");
            int handler = NewHandler(name, "function");
            bool registered = rpc.RegisterFunction(name.c_str(), kFunctions[handler]);
            result["result"] = registered;
            result["handler"] = registered ? json(handler) : json(nullptr);
            if (!registered)
                handlers_.pop_back();
        }
        else if (cmd == "rpc4RegisterSlot")
        {
            std::string name = Required<std::string>(command, "name");
            int handler = NewHandler(name, "slot");
            handlers_[handler].interrupt = Optional<bool>(command, "interrupt", false);
            rpc.RegisterSlot(name.c_str(), kFunctions[handler], static_cast<int>(IntField(command, "priority", std::numeric_limits<int>::min(), std::numeric_limits<int>::max(), 0, false)));
            result["handler"] = handler;
        }
        else if (cmd == "rpc4RegisterBlocking")
        {
            std::string name = Required<std::string>(command, "name");
            int handler = NewHandler(name, "blocking");
            if (Has(command, "returnData"))
            {
                handlers_[handler].returnData = HexField(command, "returnData");
                handlers_[handler].returnBits = BitsField(command, "returnBits", handlers_[handler].returnData);
            }
            bool registered = rpc.RegisterBlockingFunction(name.c_str(), kBlocking[handler]);
            result["result"] = registered;
            result["handler"] = registered ? json(handler) : json(nullptr);
            if (!registered)
                handlers_.pop_back();
        }
        else if (cmd == "rpc4RegisterLocalCallback")
        {
            std::string name = Required<std::string>(command, "name");
            rpc.RegisterLocalCallback(name.c_str(), static_cast<RakNet::MessageID>(IntField(command, "messageId", 0, 255, 0, true)));
        }
        else if (cmd == "rpc4UnregisterFunction")
        {
            result["result"] = rpc.UnregisterFunction(Required<std::string>(command, "name").c_str());
        }
        else if (cmd == "rpc4UnregisterBlocking")
        {
            result["result"] = rpc.UnregisterBlockingFunction(Required<std::string>(command, "name").c_str());
        }
        else if (cmd == "rpc4UnregisterLocalCallback")
        {
            result["result"] = rpc.UnregisterLocalCallback(
                Required<std::string>(command, "name").c_str(), static_cast<RakNet::MessageID>(IntField(command, "messageId", 0, 255, 0, true)));
        }
        else if (cmd == "rpc4UnregisterSlot")
        {
            result["result"] = rpc.UnregisterSlot(Required<std::string>(command, "name").c_str());
        }
        else if (cmd == "rpc4Call" || cmd == "rpc4Signal")
        {
            std::string name = Required<std::string>(command, "name");
            RakNet::BitStream stream;
            RakNet::BitStream* parameters = optionalParameters(stream);
            SendFields fields = ParseSendFields(command, RELIABLE_ORDERED);
            bool broadcast = Optional<bool>(command, "broadcast", false);
            RakNet::AddressOrGUID target = Has(command, "target") ? RakNet::AddressOrGUID(GuidField(command, "target")) : RakNet::AddressOrGUID(RakNet::UNASSIGNED_RAKNET_GUID);
            if (!broadcast && !Has(command, "target"))
                throw PluginCommandError("missing field 'target'");
            if (cmd == "rpc4Call")
                rpc.Call(name.c_str(), parameters, fields.priority, fields.reliability, fields.channel, target, broadcast);
            else
                rpc.Signal(name.c_str(), parameters, fields.priority, fields.reliability, fields.channel, target, broadcast, Optional<bool>(command, "invokeLocal", false));
        }
        else if (cmd == "rpc4CallLoopback")
        {
            std::string name = Required<std::string>(command, "name");
            RakNet::BitStream stream;
            rpc.CallLoopback(name.c_str(), optionalParameters(stream));
        }
        else if (cmd == "rpc4CallBlocking")
        {
            std::string name = Required<std::string>(command, "name");
            RakNet::BitStream stream;
            RakNet::BitStream* parameters = optionalParameters(stream);
            SendFields fields = ParseSendFields(command, RELIABLE_ORDERED);
            RakNet::BitStream returnData;
            bool called = rpc.CallBlocking(name.c_str(), parameters, fields.priority, fields.reliability, fields.channel, RakNet::AddressOrGUID(GuidField(command, "target")), &returnData);
            result["result"] = called;
            result["returnData"] = HexOf(returnData);
            result["returnBits"] = returnData.GetNumberOfBitsUsed();
        }
        else if (cmd == "rpc4Info")
        {
            ordered_json invocations = ordered_json::array();
            for (const Invocation& invocation : invocations_)
            {
                ordered_json item;
                item["handler"] = invocation.handler;
                item["name"] = handlers_[invocation.handler].name;
                item["guid"] = std::to_string(invocation.guid);
                item["address"] = invocation.address;
                item["local"] = invocation.local;
                item["parameters"] = invocation.parameters;
                item["bits"] = invocation.bits;
                invocations.push_back(item);
            }
            result["invocations"] = invocations;
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::RPC4& ReplicationCommands::RequireRpc4()
    {
        if (rpc4_ == nullptr)
            throw PluginCommandError("no RPC4 is attached; send rpc4Attach first");
        return *rpc4_;
    }

    int ReplicationCommands::NewHandler(const std::string& name, const std::string& kind)
    {
        if (handlers_.size() == kMaxHandlers)
            throw PluginCommandError("too many RPC4 registrations");
        Handler handler;
        handler.name = name;
        handler.kind = kind;
        handlers_.push_back(handler);
        return static_cast<int>(handlers_.size() - 1);
    }

    // Records the parameters a function was called with, from the stream's read position, and leaves the position as it was.
    void ReplicationCommands::RecordInvocation(int handler, RakNet::BitStream* parameters, RakNet::Packet* packet)
    {
        Invocation invocation;
        invocation.handler = handler;
        invocation.guid = packet->guid.g;
        invocation.address = AddressText(packet->systemAddress);
        invocation.local = packet->wasGeneratedLocally;
        RakNet::BitSize_t offset = parameters->GetReadOffset();
        invocation.bits = parameters->GetNumberOfUnreadBits();
        RakNet::BitStream copy;
        copy.Write(parameters, invocation.bits);
        parameters->SetReadOffset(offset);
        invocation.parameters = HexOf(copy);
        invocations_.push_back(invocation);
        if (handlers_[handler].interrupt && rpc4_ != nullptr)
            rpc4_->InterruptSignal();
    }

    void ReplicationCommands::RecordBlockingInvocation(int handler, RakNet::BitStream* parameters, RakNet::BitStream* returnData, RakNet::Packet* packet)
    {
        RecordInvocation(handler, parameters, packet);
        const Handler& registered = handlers_[handler];
        WriteHexBits(*returnData, registered.returnData, registered.returnBits);
    }
}
