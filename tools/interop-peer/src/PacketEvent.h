#pragma once

#include <nlohmann/json.hpp>

#include <cstddef>
#include <cstdint>
#include <string>

namespace interop
{
    // Builds the "packet" event described in docs/interop-protocol.md from the raw fields
    // of a RakNet::Packet. Kept free of RakPeer so it can be tested without sockets.
    nlohmann::ordered_json DescribePacket(
        const unsigned char* data,
        std::size_t length,
        std::uint64_t guid,
        const std::string& address,
        std::uint32_t timeMs);
}
