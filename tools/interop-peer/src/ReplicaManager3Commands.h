#pragma once

#include <nlohmann/json.hpp>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace RakNet
{
    class NetworkIDManager;
    class RakPeerInterface;
}

namespace interop
{
    // The rm3* commands of docs/interop-protocol.md: a ReplicaManager3 with the replicas of Samples/ReplicaManager3 and
    // the User of Samples/ReplicatedLogin. Errors are thrown as PluginCommandError. Every method is called with the
    // peer's lock held, the lock Receive() runs under.
    class ReplicaManager3Commands
    {
    public:
        enum class Topology
        {
            Client,
            Server,
            P2P,
        };

        // A destruction DeserializeDestruction read.
        struct Destruction
        {
            std::uint64_t networkId;
            std::string kind;
            std::string text;
            std::uint64_t deletingGuid;
        };

        explicit ReplicaManager3Commands(RakNet::RakPeerInterface* peer);
        ~ReplicaManager3Commands();

        ReplicaManager3Commands(const ReplicaManager3Commands&) = delete;
        ReplicaManager3Commands& operator=(const ReplicaManager3Commands&) = delete;

        // Runs cmd when it is one of these commands and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        Topology GetTopology() const { return topology_; }

        void RecordDestruction(const Destruction& destruction) { destructions_.push_back(destruction); }

        class Manager;

    private:
        class ReceiptForwarder;

        Manager& RequireManager();

        RakNet::RakPeerInterface* peer_;
        Topology topology_ = Topology::Server;
        std::vector<Destruction> destructions_;
        // Declared before the manager, so the manager clears its replicas' IDs while the NetworkIDManager exists.
        std::unique_ptr<RakNet::NetworkIDManager> networkIdManager_;
        std::unique_ptr<Manager> manager_;
        std::unique_ptr<ReceiptForwarder> receipts_;
    };
}
