#pragma once

#include <nlohmann/json.hpp>

#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

namespace RakNet
{
    class MessageFilter;
    class RakPeerInterface;
}

namespace interop
{
    // A plugin command the peer could not carry out; reported as {"ok":false,"error":...}.
    struct PluginCommandError : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // The commands that attach and drive RakNet plugins on the peer, specified in docs/interop-protocol.md.
    // Every method is called with the peer's lock held, the lock Receive() runs under, so plugin state is
    // touched by one thread at a time.
    class PluginCommands
    {
    public:
        explicit PluginCommands(RakNet::RakPeerInterface* peer);
        ~PluginCommands();

        PluginCommands(const PluginCommands&) = delete;
        PluginCommands& operator=(const PluginCommands&) = delete;

        // Runs cmd when it is a plugin command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        struct FilterEvent
        {
            std::uint64_t guid;
            std::string address;
            int filterSetId;
            int messageId; // -1 for a timeout
        };

    private:
        RakNet::MessageFilter& RequireMessageFilter();
        void TrackFilterSet(int filterSetId);
        void MessageFilterInfo(nlohmann::ordered_json& result);

        RakNet::RakPeerInterface* peer_;
        RakNet::MessageFilter* messageFilter_ = nullptr;
        std::vector<FilterEvent> disallowed_;
        std::vector<FilterEvent> timeouts_;
    };
}
