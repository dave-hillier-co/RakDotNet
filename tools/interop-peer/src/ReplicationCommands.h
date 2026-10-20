#pragma once

#include <nlohmann/json.hpp>

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <vector>

class GridSectorizer;

namespace RakNet
{
    class BitStream;
    class NetworkIDManager;
    class RakNetRandomSync;
    class RakPeerInterface;
    class RPC4;
    class VariableDeltaSerializer;
    struct Packet;
}

namespace interop
{
    // The object replication and RPC commands of docs/interop-protocol.md: RandSync, NetworkIDManager and
    // NetworkIDObject, GridSectorizer, VariableDeltaSerializer and RPC4. Errors are thrown as PluginCommandError.
    // Every method is called with the peer's lock held, the lock Receive() runs under.
    class ReplicationCommands
    {
    public:
        explicit ReplicationCommands(RakNet::RakPeerInterface* peer);
        ~ReplicationCommands();

        ReplicationCommands(const ReplicationCommands&) = delete;
        ReplicationCommands& operator=(const ReplicationCommands&) = delete;

        // Runs cmd when it is one of these commands and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        // Called by the RPC4 functions the peer registers; handler is the registration's index.
        void RecordInvocation(int handler, RakNet::BitStream* parameters, RakNet::Packet* packet);
        void RecordBlockingInvocation(int handler, RakNet::BitStream* parameters, RakNet::BitStream* returnData, RakNet::Packet* packet);

        struct Handler
        {
            std::string name;
            std::string kind;
            bool interrupt = false;
            std::vector<unsigned char> returnData;
            unsigned returnBits = 0;
        };

        struct Invocation
        {
            int handler;
            std::uint64_t guid;
            std::string address;
            bool local;
            std::string parameters;
            unsigned bits;
        };

    private:
        bool DispatchRandSync(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchNetworkId(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchGrid(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchDelta(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        bool DispatchRpc4(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);
        void DeltaSerialize(const nlohmann::json& command, nlohmann::ordered_json& result);
        RakNet::RPC4& RequireRpc4();
        GridSectorizer& RequireGrid();
        int NewHandler(const std::string& name, const std::string& kind);

        class Object;

        RakNet::RakPeerInterface* peer_;
        std::unique_ptr<RakNet::RakNetRandomSync> randSync_;
        // Declared before the objects, so objects untrack themselves before the manager goes.
        std::unique_ptr<RakNet::NetworkIDManager> networkIdManager_;
        std::map<int, std::unique_ptr<Object>> objects_;
        std::unique_ptr<GridSectorizer> grid_;
        std::unique_ptr<RakNet::VariableDeltaSerializer> deltaSerializer_;
        RakNet::RPC4* rpc4_ = nullptr;
        std::vector<Handler> handlers_;
        std::vector<Invocation> invocations_;
    };
}
