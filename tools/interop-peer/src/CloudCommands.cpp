#include "CloudCommands.h"

#include "Hex.h"
#include "PluginCommands.h"

#include "CloudClient.h"
#include "CloudServer.h"
#include "MessageIdentifiers.h"
#include "PluginInterface2.h"
#include "RakPeerInterface.h"

#include <cerrno>
#include <cstdint>
#include <cstdlib>
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

        long long IntField(const json& command, const char* field, long long minimum, long long maximum, long long fallback)
        {
            long long value = Optional<long long>(command, field, fallback);
            if (value < minimum || value > maximum)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return value;
        }

        RakNet::RakNetGUID ParseGuid(const json& value, const char* field)
        {
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

        RakNet::RakNetGUID GuidField(const json& command, const char* field)
        {
            return ParseGuid(Required<json>(command, field), field);
        }

        RakNet::CloudKey KeyJson(const json& key)
        {
            return RakNet::CloudKey(
                RakNet::RakString(Required<std::string>(key, "primaryKey").c_str()),
                static_cast<uint32_t>(IntField(key, "secondaryKey", 0, 0xffffffffLL, 0)));
        }

        DataStructures::List<RakNet::CloudKey> KeysField(const json& command)
        {
            DataStructures::List<RakNet::CloudKey> keys;
            for (const json& key : Required<json>(command, "keys"))
                keys.Push(KeyJson(key), _FILE_AND_LINE_);
            return keys;
        }

        // Absent means the overload without specific systems.
        bool SpecificSystemsField(const json& command, DataStructures::List<RakNet::RakNetGUID>& systems)
        {
            auto it = command.find("specificSystems");
            if (it == command.end() || it->is_null())
                return false;
            for (const json& guid : *it)
                systems.Push(ParseGuid(guid, "specificSystems"), _FILE_AND_LINE_);
            return true;
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        // The primary key as hex: a RakString need not be UTF-8.
        ordered_json KeyRecord(const RakNet::CloudKey& key)
        {
            ordered_json result;
            result["primaryKey"] = ToHex(reinterpret_cast<const unsigned char*>(key.primaryKey.C_String()), key.primaryKey.GetLength());
            result["secondaryKey"] = key.secondaryKey;
            return result;
        }

        ordered_json KeysRecord(const DataStructures::List<RakNet::CloudKey>& keys)
        {
            ordered_json result = ordered_json::array();
            for (unsigned int i = 0; i < keys.Size(); i++)
                result.push_back(KeyRecord(keys[i]));
            return result;
        }

        ordered_json GuidsRecord(const DataStructures::List<RakNet::RakNetGUID>& guids)
        {
            ordered_json result = ordered_json::array();
            for (unsigned int i = 0; i < guids.Size(); i++)
                result.push_back(std::to_string(guids[i].g));
            return result;
        }

        ordered_json RowRecord(const RakNet::CloudQueryRow& row)
        {
            ordered_json result = KeyRecord(row.key);
            result["data"] = row.data != nullptr ? ToHex(row.data, row.length) : std::string();
            result["serverAddress"] = AddressText(row.serverSystemAddress);
            result["clientAddress"] = AddressText(row.clientSystemAddress);
            result["serverGuid"] = std::to_string(row.serverGUID.g);
            result["clientGuid"] = std::to_string(row.clientGUID.g);
            return result;
        }

        // CloudClientCallback recording what CloudClient decoded.
        class RecordingCallback : public RakNet::CloudClientCallback
        {
        public:
            explicit RecordingCallback(CloudCommands::Records& records) : records_(records) {}

            void OnGet(RakNet::CloudQueryResult* result, bool* deallocateRowsAfterReturn) override
            {
                (void)deallocateRowsAfterReturn;
                ordered_json record;
                record["keys"] = KeysRecord(result->cloudQuery.keys);
                record["startingRowIndex"] = result->cloudQuery.startingRowIndex;
                record["maxRowsToReturn"] = result->cloudQuery.maxRowsToReturn;
                record["querySubscribe"] = result->cloudQuery.subscribeToResults;
                record["subscribeToResults"] = result->subscribeToResults;
                ordered_json rows = ordered_json::array();
                for (unsigned int i = 0; i < result->rowsReturned.Size(); i++)
                    rows.push_back(RowRecord(*result->rowsReturned[i]));
                record["rows"] = rows;
                records_.gets.push_back(record);
            }

            void OnSubscriptionNotification(RakNet::CloudQueryRow* result, bool wasUpdated, bool* deallocateRowAfterReturn) override
            {
                (void)deallocateRowAfterReturn;
                ordered_json record;
                record["wasUpdated"] = wasUpdated;
                record["row"] = RowRecord(*result);
                records_.notifications.push_back(record);
            }

        private:
            CloudCommands::Records& records_;
        };
    }

    // Passes ID_CLOUD_GET_RESPONSE and ID_CLOUD_SUBSCRIPTION_NOTIFICATION to the CloudClient with a recording callback,
    // as Samples/CloudTest does, and leaves them for the application.
    class CloudRecorder : public RakNet::PluginInterface2
    {
    public:
        CloudRecorder(RakNet::CloudClient* client, CloudCommands::Records& records) : client_(client), callback_(records) {}

        RakNet::PluginReceiveResult OnReceive(RakNet::Packet* packet) override
        {
            if (packet->length > 0 && packet->data[0] == ID_CLOUD_GET_RESPONSE)
                client_->OnGetReponse(packet, &callback_);
            else if (packet->length > 0 && packet->data[0] == ID_CLOUD_SUBSCRIPTION_NOTIFICATION)
                client_->OnSubscriptionNotification(packet, &callback_);
            return RakNet::RR_CONTINUE_PROCESSING;
        }

    private:
        RakNet::CloudClient* client_;
        RecordingCallback callback_;
    };

    // Samples/CloudServer's CloudServerHelperFilter: posts under one primary key are allowed only from one system. Every
    // call is recorded.
    class CloudFilter : public RakNet::CloudServerQueryFilter
    {
    public:
        CloudFilter(CloudCommands::Records& records, const std::string& rejectPostPrimaryKey, RakNet::RakNetGUID exempt)
            : records_(records), rejectPostPrimaryKey_(rejectPostPrimaryKey), exempt_(exempt) {}

        bool OnPostRequest(RakNet::RakNetGUID clientGuid, RakNet::SystemAddress clientAddress, RakNet::CloudKey key, uint32_t dataLength, const char* data) override
        {
            ordered_json record = Call("post", clientGuid, clientAddress);
            record["key"] = KeyRecord(key);
            record["data"] = ToHex(reinterpret_cast<const unsigned char*>(data), dataLength);
            records_.filterCalls.push_back(record);
            return clientGuid == exempt_ || rejectPostPrimaryKey_.empty() || key.primaryKey != rejectPostPrimaryKey_.c_str();
        }

        bool OnReleaseRequest(RakNet::RakNetGUID clientGuid, RakNet::SystemAddress clientAddress, DataStructures::List<RakNet::CloudKey>& cloudKeys) override
        {
            ordered_json record = Call("release", clientGuid, clientAddress);
            record["keys"] = KeysRecord(cloudKeys);
            records_.filterCalls.push_back(record);
            return true;
        }

        bool OnGetRequest(RakNet::RakNetGUID clientGuid, RakNet::SystemAddress clientAddress, RakNet::CloudQuery& query, DataStructures::List<RakNet::RakNetGUID>& specificSystems) override
        {
            ordered_json record = Call("get", clientGuid, clientAddress);
            record["keys"] = KeysRecord(query.keys);
            record["specificSystems"] = GuidsRecord(specificSystems);
            records_.filterCalls.push_back(record);
            return true;
        }

        bool OnUnsubscribeRequest(RakNet::RakNetGUID clientGuid, RakNet::SystemAddress clientAddress, DataStructures::List<RakNet::CloudKey>& cloudKeys, DataStructures::List<RakNet::RakNetGUID>& specificSystems) override
        {
            ordered_json record = Call("unsubscribe", clientGuid, clientAddress);
            record["keys"] = KeysRecord(cloudKeys);
            record["specificSystems"] = GuidsRecord(specificSystems);
            records_.filterCalls.push_back(record);
            return true;
        }

    private:
        static ordered_json Call(const char* op, RakNet::RakNetGUID clientGuid, const RakNet::SystemAddress& clientAddress)
        {
            ordered_json record;
            record["op"] = op;
            record["clientGuid"] = std::to_string(clientGuid.g);
            record["clientAddress"] = AddressText(clientAddress);
            return record;
        }

        CloudCommands::Records& records_;
        std::string rejectPostPrimaryKey_;
        RakNet::RakNetGUID exempt_;
    };

    CloudCommands::CloudCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    CloudCommands::~CloudCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here.
        if (server_ != nullptr)
        {
            server_->RemoveAllQueryFilters();
            delete server_;
        }
        if (client_ != nullptr)
            RakNet::CloudClient::DestroyInstance(client_);
    }

    bool CloudCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "cloudClientAttach")
        {
            if (client_ == nullptr)
            {
                client_ = RakNet::CloudClient::GetInstance();
                peer_->AttachPlugin(client_);
                recorder_.reset(new CloudRecorder(client_, records_));
                peer_->AttachPlugin(recorder_.get());
            }
        }
        else if (cmd == "cloudServerAttach")
        {
            if (server_ == nullptr)
            {
                // CloudServer.cpp has no STATIC_FACTORY_DEFINITIONS, so its GetInstance is never defined.
                server_ = new RakNet::CloudServer();
                peer_->AttachPlugin(server_);
            }
        }
        else if (cmd == "cloudPost")
        {
            RakNet::CloudClient& client = RequireClient();
            RakNet::CloudKey key = KeyJson(Required<json>(command, "key"));
            std::vector<unsigned char> data;
            if (!TryFromHex(Optional<std::string>(command, "data", ""), data))
                throw PluginCommandError("field 'data' is not hex");
            client.Post(&key, data.empty() ? nullptr : data.data(), static_cast<uint32_t>(data.size()), GuidField(command, "target"));
        }
        else if (cmd == "cloudRelease")
        {
            DataStructures::List<RakNet::CloudKey> keys = KeysField(command);
            RequireClient().Release(keys, GuidField(command, "target"));
        }
        else if (cmd == "cloudGet")
        {
            RakNet::CloudClient& client = RequireClient();
            RakNet::CloudQuery query;
            query.keys = KeysField(command);
            query.startingRowIndex = static_cast<uint32_t>(IntField(command, "startingRowIndex", 0, 0xffffffffLL, 0));
            query.maxRowsToReturn = static_cast<uint32_t>(IntField(command, "maxRowsToReturn", 0, 0xffffffffLL, 0));
            query.subscribeToResults = Optional<bool>(command, "subscribe", false);
            DataStructures::List<RakNet::RakNetGUID> systems;
            if (SpecificSystemsField(command, systems))
                client.Get(&query, systems, GuidField(command, "target"));
            else
                client.Get(&query, GuidField(command, "target"));
        }
        else if (cmd == "cloudUnsubscribe")
        {
            RakNet::CloudClient& client = RequireClient();
            DataStructures::List<RakNet::CloudKey> keys = KeysField(command);
            DataStructures::List<RakNet::RakNetGUID> systems;
            if (SpecificSystemsField(command, systems))
                client.Unsubscribe(keys, systems, GuidField(command, "target"));
            else
                client.Unsubscribe(keys, GuidField(command, "target"));
        }
        else if (cmd == "cloudServerAddServer")
        {
            RequireServer().AddServer(GuidField(command, "guid"));
        }
        else if (cmd == "cloudServerRemoveServer")
        {
            RequireServer().RemoveServer(GuidField(command, "guid"));
        }
        else if (cmd == "cloudServerSetMaxUploadBytesPerClient")
        {
            RequireServer().SetMaxUploadBytesPerClient(static_cast<uint64_t>(IntField(command, "bytes", 0, 0x7fffffffffffffffLL, 0)));
        }
        else if (cmd == "cloudServerForceExternalAddress")
        {
            RakNet::SystemAddress address;
            if (!address.FromString(Required<std::string>(command, "host").c_str()))
                throw PluginCommandError("field 'host' is not an address");
            address.SetPortHostOrder(static_cast<unsigned short>(IntField(command, "port", 0, 65535, 0)));
            RequireServer().ForceExternalSystemAddress(address);
        }
        else if (cmd == "cloudServerSetFilter")
        {
            RakNet::CloudServer& server = RequireServer();
            RakNet::RakNetGUID exempt = RakNet::UNASSIGNED_RAKNET_GUID;
            if (command.contains("exemptGuid") && !command["exemptGuid"].is_null())
                exempt = GuidField(command, "exemptGuid");
            server.RemoveAllQueryFilters();
            filter_.reset(new CloudFilter(records_, Optional<std::string>(command, "rejectPostPrimaryKey", ""), exempt));
            server.AddQueryFilter(filter_.get());
        }
        else if (cmd == "cloudInfo")
        {
            result["gets"] = records_.gets;
            result["notifications"] = records_.notifications;
            result["filterCalls"] = records_.filterCalls;
            ordered_json servers = ordered_json::array();
            if (server_ != nullptr)
            {
                DataStructures::List<RakNet::RakNetGUID> remote;
                server_->GetRemoteServers(remote);
                servers = GuidsRecord(remote);
            }
            result["remoteServers"] = servers;
            result["localIp"] = std::string(peer_->GetLocalIP(0));
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::CloudClient& CloudCommands::RequireClient()
    {
        if (client_ == nullptr)
            throw PluginCommandError("no CloudClient is attached; send cloudClientAttach first");
        return *client_;
    }

    RakNet::CloudServer& CloudCommands::RequireServer()
    {
        if (server_ == nullptr)
            throw PluginCommandError("no CloudServer is attached; send cloudServerAttach first");
        return *server_;
    }
}
