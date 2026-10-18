#include "PacketEvent.h"

#include "Hex.h"
#include "Names.h"

#include "MessageIdentifiers.h"

namespace interop
{
    namespace
    {
        // RakNet::Time is 64-bit (RakNetDefines.h:23, RakNetTime.h:21). BitStream swaps
        // multi-byte values to network order on little-endian hosts unless
        // __BITSTREAM_NATIVE_END is defined (BitStream.h:884, :1092), so the timestamp
        // after ID_TIMESTAMP is big-endian.
        constexpr std::size_t kTimeSize = 8;
    }

    nlohmann::ordered_json DescribePacket(
        const unsigned char* data,
        std::size_t length,
        std::uint64_t guid,
        const std::string& address,
        std::uint32_t timeMs)
    {
        nlohmann::ordered_json event;
        event["event"] = "packet";

        if (length == 0)
        {
            event["messageId"] = nullptr;
            event["messageName"] = nullptr;
        }
        else
        {
            unsigned char messageId = data[0];
            bool timestamped = data[0] == ID_TIMESTAMP && length >= 1 + kTimeSize + 1;
            if (timestamped)
                messageId = data[1 + kTimeSize];

            event["messageId"] = messageId;
            event["messageName"] = MessageName(messageId);

            if (timestamped)
            {
                std::uint64_t timestamp = 0;
                for (std::size_t i = 0; i < kTimeSize; ++i)
                    timestamp = (timestamp << 8) | data[1 + i];
                event["timestamp"] = std::to_string(timestamp);
            }
        }

        event["guid"] = std::to_string(guid);
        event["address"] = address;
        event["data"] = ToHex(data, length);
        event["timeMs"] = timeMs;
        return event;
    }
}
