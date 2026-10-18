// Tests for the peer's pure translation logic (hex, names, packet events).
// A tiny runner keeps the tool free of test-framework dependencies; run via ctest.

#include "Hex.h"
#include "Names.h"
#include "PacketEvent.h"

#include "MessageIdentifiers.h"

#include <cstdio>
#include <functional>
#include <string>
#include <vector>

namespace
{
    int failures = 0;

    void Check(bool condition, const char* expression, const char* file, int line)
    {
        if (!condition)
        {
            ++failures;
            std::fprintf(stderr, "%s:%d: check failed: %s\n", file, line, expression);
        }
    }

#define CHECK(condition) Check((condition), #condition, __FILE__, __LINE__)

    struct TestCase
    {
        const char* name;
        std::function<void()> body;
    };

    std::vector<TestCase>& Tests()
    {
        static std::vector<TestCase> tests;
        return tests;
    }

    struct Register
    {
        Register(const char* name, std::function<void()> body) { Tests().push_back({name, std::move(body)}); }
    };

#define TEST(name)                          \
    void name();                            \
    const Register register_##name(#name, name); \
    void name()

    TEST(Hex_encoding_is_lowercase_without_separators)
    {
        const unsigned char bytes[] = {0x86, 0x01, 0xff, 0x0a};
        CHECK(interop::ToHex(bytes, sizeof(bytes)) == "8601ff0a");
    }

    TEST(Hex_decoding_accepts_either_case)
    {
        std::vector<unsigned char> bytes;
        CHECK(interop::TryFromHex("8601FFab", bytes));
        CHECK((bytes == std::vector<unsigned char>{0x86, 0x01, 0xff, 0xab}));
    }

    TEST(Hex_decoding_rejects_odd_length_and_non_hex_characters)
    {
        std::vector<unsigned char> bytes;
        CHECK(!interop::TryFromHex("abc", bytes));
        CHECK(!interop::TryFromHex("zz", bytes));
        CHECK(bytes.empty());
    }

    TEST(Hex_decoding_of_empty_text_is_empty)
    {
        std::vector<unsigned char> bytes{1};
        CHECK(interop::TryFromHex("", bytes));
        CHECK(bytes.empty());
    }

    TEST(Message_names_follow_the_cpp_enum)
    {
        CHECK(interop::MessageName(0) == "ID_CONNECTED_PING");
        CHECK(interop::MessageName(ID_TIMESTAMP) == "ID_TIMESTAMP");
        CHECK(interop::MessageName(28) == "ID_UNCONNECTED_PONG");
        CHECK(interop::MessageName(ID_USER_PACKET_ENUM - 1) == "ID_RESERVED_9");
    }

    TEST(User_message_names_are_offsets_from_ID_USER_PACKET_ENUM)
    {
        CHECK(interop::MessageName(134) == "ID_USER_PACKET_ENUM+0");
        CHECK(interop::MessageName(255) == "ID_USER_PACKET_ENUM+121");
    }

    TEST(Reliability_and_priority_names_parse_to_cpp_values)
    {
        PacketReliability reliability = UNRELIABLE;
        CHECK(interop::TryParseReliability("RELIABLE_ORDERED_WITH_ACK_RECEIPT", reliability));
        CHECK(reliability == RELIABLE_ORDERED_WITH_ACK_RECEIPT);
        CHECK(!interop::TryParseReliability("ReliableOrdered", reliability));

        PacketPriority priority = LOW_PRIORITY;
        CHECK(interop::TryParsePriority("IMMEDIATE_PRIORITY", priority));
        CHECK(priority == IMMEDIATE_PRIORITY);
        CHECK(!interop::TryParsePriority("HIGH", priority));
    }

    TEST(Packet_event_reports_the_first_byte_as_message_id)
    {
        const unsigned char data[] = {0x86, 0x01, 0x02, 0x03};
        nlohmann::ordered_json event = interop::DescribePacket(data, sizeof(data), 1234567890ull, "127.0.0.1:61234", 12345);
        CHECK(event.dump() ==
              R"({"event":"packet","messageId":134,"messageName":"ID_USER_PACKET_ENUM+0","guid":"1234567890",)"
              R"("address":"127.0.0.1:61234","data":"86010203","timeMs":12345})");
    }

    TEST(Packet_event_reads_through_ID_TIMESTAMP_to_the_real_message_id)
    {
        const unsigned char data[] = {ID_TIMESTAMP, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x87, 0xaa};
        nlohmann::ordered_json event = interop::DescribePacket(data, sizeof(data), 1, "127.0.0.1:1", 0);
        CHECK(event["messageId"] == 135);
        CHECK(event["messageName"] == "ID_USER_PACKET_ENUM+1");
        CHECK(event["timestamp"] == "66051");
        CHECK(event["data"] == "1b000000000001020387aa");
    }

    TEST(Packet_event_guid_is_a_decimal_string_beyond_double_precision)
    {
        const unsigned char data[] = {0x86};
        nlohmann::ordered_json event = interop::DescribePacket(data, sizeof(data), 18446744073709551615ull, "127.0.0.1:1", 0);
        CHECK(event["guid"] == "18446744073709551615");
    }

    TEST(Truncated_ID_TIMESTAMP_packet_is_reported_as_ID_TIMESTAMP_without_timestamp)
    {
        const unsigned char data[] = {ID_TIMESTAMP, 0x00, 0x01};
        nlohmann::ordered_json event = interop::DescribePacket(data, sizeof(data), 1, "127.0.0.1:1", 0);
        CHECK(event["messageId"] == ID_TIMESTAMP);
        CHECK(!event.contains("timestamp"));
    }
}

int main()
{
    for (const TestCase& test : Tests())
    {
        int before = failures;
        test.body();
        std::printf("%s %s\n", failures == before ? "PASS" : "FAIL", test.name);
    }
    std::printf("%zu tests, %d failed checks\n", Tests().size(), failures);
    return failures == 0 ? 0 : 1;
}
