#include "Names.h"

#include "MessageIdentifiers.h"
#include "RakNetStatistics.h"

#include <cstring>

namespace interop
{
    namespace
    {
// Every DefaultMessageIDTypes enumerator below ID_USER_PACKET_ENUM, in declaration order
// (MessageIdentifiers.h:51-430). Extracted from the header; the static_assert below checks
// that each name's value equals its position, so a mismatch fails the build.
#define INTEROP_MESSAGE_IDS(X) \
    X(ID_CONNECTED_PING) \
    X(ID_UNCONNECTED_PING) \
    X(ID_UNCONNECTED_PING_OPEN_CONNECTIONS) \
    X(ID_CONNECTED_PONG) \
    X(ID_DETECT_LOST_CONNECTIONS) \
    X(ID_OPEN_CONNECTION_REQUEST_1) \
    X(ID_OPEN_CONNECTION_REPLY_1) \
    X(ID_OPEN_CONNECTION_REQUEST_2) \
    X(ID_OPEN_CONNECTION_REPLY_2) \
    X(ID_CONNECTION_REQUEST) \
    X(ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY) \
    X(ID_OUR_SYSTEM_REQUIRES_SECURITY) \
    X(ID_PUBLIC_KEY_MISMATCH) \
    X(ID_OUT_OF_BAND_INTERNAL) \
    X(ID_SND_RECEIPT_ACKED) \
    X(ID_SND_RECEIPT_LOSS) \
    X(ID_CONNECTION_REQUEST_ACCEPTED) \
    X(ID_CONNECTION_ATTEMPT_FAILED) \
    X(ID_ALREADY_CONNECTED) \
    X(ID_NEW_INCOMING_CONNECTION) \
    X(ID_NO_FREE_INCOMING_CONNECTIONS) \
    X(ID_DISCONNECTION_NOTIFICATION) \
    X(ID_CONNECTION_LOST) \
    X(ID_CONNECTION_BANNED) \
    X(ID_INVALID_PASSWORD) \
    X(ID_INCOMPATIBLE_PROTOCOL_VERSION) \
    X(ID_IP_RECENTLY_CONNECTED) \
    X(ID_TIMESTAMP) \
    X(ID_UNCONNECTED_PONG) \
    X(ID_ADVERTISE_SYSTEM) \
    X(ID_DOWNLOAD_PROGRESS) \
    X(ID_REMOTE_DISCONNECTION_NOTIFICATION) \
    X(ID_REMOTE_CONNECTION_LOST) \
    X(ID_REMOTE_NEW_INCOMING_CONNECTION) \
    X(ID_FILE_LIST_TRANSFER_HEADER) \
    X(ID_FILE_LIST_TRANSFER_FILE) \
    X(ID_FILE_LIST_REFERENCE_PUSH_ACK) \
    X(ID_DDT_DOWNLOAD_REQUEST) \
    X(ID_TRANSPORT_STRING) \
    X(ID_REPLICA_MANAGER_CONSTRUCTION) \
    X(ID_REPLICA_MANAGER_SCOPE_CHANGE) \
    X(ID_REPLICA_MANAGER_SERIALIZE) \
    X(ID_REPLICA_MANAGER_DOWNLOAD_STARTED) \
    X(ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE) \
    X(ID_RAKVOICE_OPEN_CHANNEL_REQUEST) \
    X(ID_RAKVOICE_OPEN_CHANNEL_REPLY) \
    X(ID_RAKVOICE_CLOSE_CHANNEL) \
    X(ID_RAKVOICE_DATA) \
    X(ID_AUTOPATCHER_GET_CHANGELIST_SINCE_DATE) \
    X(ID_AUTOPATCHER_CREATION_LIST) \
    X(ID_AUTOPATCHER_DELETION_LIST) \
    X(ID_AUTOPATCHER_GET_PATCH) \
    X(ID_AUTOPATCHER_PATCH_LIST) \
    X(ID_AUTOPATCHER_REPOSITORY_FATAL_ERROR) \
    X(ID_AUTOPATCHER_CANNOT_DOWNLOAD_ORIGINAL_UNMODIFIED_FILES) \
    X(ID_AUTOPATCHER_FINISHED_INTERNAL) \
    X(ID_AUTOPATCHER_FINISHED) \
    X(ID_AUTOPATCHER_RESTART_APPLICATION) \
    X(ID_NAT_PUNCHTHROUGH_REQUEST) \
    X(ID_NAT_CONNECT_AT_TIME) \
    X(ID_NAT_GET_MOST_RECENT_PORT) \
    X(ID_NAT_CLIENT_READY) \
    X(ID_NAT_TARGET_NOT_CONNECTED) \
    X(ID_NAT_TARGET_UNRESPONSIVE) \
    X(ID_NAT_CONNECTION_TO_TARGET_LOST) \
    X(ID_NAT_ALREADY_IN_PROGRESS) \
    X(ID_NAT_PUNCHTHROUGH_FAILED) \
    X(ID_NAT_PUNCHTHROUGH_SUCCEEDED) \
    X(ID_READY_EVENT_SET) \
    X(ID_READY_EVENT_UNSET) \
    X(ID_READY_EVENT_ALL_SET) \
    X(ID_READY_EVENT_QUERY) \
    X(ID_LOBBY_GENERAL) \
    X(ID_RPC_REMOTE_ERROR) \
    X(ID_RPC_PLUGIN) \
    X(ID_FILE_LIST_REFERENCE_PUSH) \
    X(ID_READY_EVENT_FORCE_ALL_SET) \
    X(ID_ROOMS_EXECUTE_FUNC) \
    X(ID_ROOMS_LOGON_STATUS) \
    X(ID_ROOMS_HANDLE_CHANGE) \
    X(ID_LOBBY2_SEND_MESSAGE) \
    X(ID_LOBBY2_SERVER_ERROR) \
    X(ID_FCM2_NEW_HOST) \
    X(ID_FCM2_REQUEST_FCMGUID) \
    X(ID_FCM2_RESPOND_CONNECTION_COUNT) \
    X(ID_FCM2_INFORM_FCMGUID) \
    X(ID_FCM2_UPDATE_MIN_TOTAL_CONNECTION_COUNT) \
    X(ID_FCM2_VERIFIED_JOIN_START) \
    X(ID_FCM2_VERIFIED_JOIN_CAPABLE) \
    X(ID_FCM2_VERIFIED_JOIN_FAILED) \
    X(ID_FCM2_VERIFIED_JOIN_ACCEPTED) \
    X(ID_FCM2_VERIFIED_JOIN_REJECTED) \
    X(ID_UDP_PROXY_GENERAL) \
    X(ID_SQLite3_EXEC) \
    X(ID_SQLite3_UNKNOWN_DB) \
    X(ID_SQLLITE_LOGGER) \
    X(ID_NAT_TYPE_DETECTION_REQUEST) \
    X(ID_NAT_TYPE_DETECTION_RESULT) \
    X(ID_ROUTER_2_INTERNAL) \
    X(ID_ROUTER_2_FORWARDING_NO_PATH) \
    X(ID_ROUTER_2_FORWARDING_ESTABLISHED) \
    X(ID_ROUTER_2_REROUTED) \
    X(ID_TEAM_BALANCER_INTERNAL) \
    X(ID_TEAM_BALANCER_REQUESTED_TEAM_FULL) \
    X(ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED) \
    X(ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED) \
    X(ID_TEAM_BALANCER_TEAM_ASSIGNED) \
    X(ID_LIGHTSPEED_INTEGRATION) \
    X(ID_XBOX_LOBBY) \
    X(ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_SUCCESS) \
    X(ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS) \
    X(ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_FAILURE) \
    X(ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_FAILURE) \
    X(ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_TIMEOUT) \
    X(ID_TWO_WAY_AUTHENTICATION_NEGOTIATION) \
    X(ID_CLOUD_POST_REQUEST) \
    X(ID_CLOUD_RELEASE_REQUEST) \
    X(ID_CLOUD_GET_REQUEST) \
    X(ID_CLOUD_GET_RESPONSE) \
    X(ID_CLOUD_UNSUBSCRIBE_REQUEST) \
    X(ID_CLOUD_SERVER_TO_SERVER_COMMAND) \
    X(ID_CLOUD_SUBSCRIPTION_NOTIFICATION) \
    X(ID_LIB_VOICE) \
    X(ID_RELAY_PLUGIN) \
    X(ID_NAT_REQUEST_BOUND_ADDRESSES) \
    X(ID_NAT_RESPOND_BOUND_ADDRESSES) \
    X(ID_FCM2_UPDATE_USER_CONTEXT) \
    X(ID_RESERVED_3) \
    X(ID_RESERVED_4) \
    X(ID_RESERVED_5) \
    X(ID_RESERVED_6) \
    X(ID_RESERVED_7) \
    X(ID_RESERVED_8) \
    X(ID_RESERVED_9) \

#define INTEROP_AS_NAME(id) #id,
#define INTEROP_AS_VALUE(id) static_cast<int>(id),

        const char* const kMessageNames[] = {INTEROP_MESSAGE_IDS(INTEROP_AS_NAME)};
        constexpr int kMessageValues[] = {INTEROP_MESSAGE_IDS(INTEROP_AS_VALUE)};

#undef INTEROP_AS_NAME
#undef INTEROP_AS_VALUE
#undef INTEROP_MESSAGE_IDS

        constexpr bool MessageValuesMatchPositions()
        {
            for (int i = 0; i < static_cast<int>(sizeof(kMessageValues) / sizeof(kMessageValues[0])); ++i)
            {
                if (kMessageValues[i] != i)
                    return false;
            }
            return true;
        }

        static_assert(sizeof(kMessageValues) / sizeof(kMessageValues[0]) == ID_USER_PACKET_ENUM,
                      "message name table must cover every ID below ID_USER_PACKET_ENUM");
        static_assert(MessageValuesMatchPositions(), "message name table is out of order");

        struct ReliabilityName
        {
            const char* name;
            PacketReliability value;
        };

        const ReliabilityName kReliabilities[] = {
            {"UNRELIABLE", UNRELIABLE},
            {"UNRELIABLE_SEQUENCED", UNRELIABLE_SEQUENCED},
            {"RELIABLE", RELIABLE},
            {"RELIABLE_ORDERED", RELIABLE_ORDERED},
            {"RELIABLE_SEQUENCED", RELIABLE_SEQUENCED},
            {"UNRELIABLE_WITH_ACK_RECEIPT", UNRELIABLE_WITH_ACK_RECEIPT},
            {"RELIABLE_WITH_ACK_RECEIPT", RELIABLE_WITH_ACK_RECEIPT},
            {"RELIABLE_ORDERED_WITH_ACK_RECEIPT", RELIABLE_ORDERED_WITH_ACK_RECEIPT},
        };

        struct PriorityName
        {
            const char* name;
            PacketPriority value;
        };

        const PriorityName kPriorities[] = {
            {"IMMEDIATE_PRIORITY", IMMEDIATE_PRIORITY},
            {"HIGH_PRIORITY", HIGH_PRIORITY},
            {"MEDIUM_PRIORITY", MEDIUM_PRIORITY},
            {"LOW_PRIORITY", LOW_PRIORITY},
        };
    }

    std::string MessageName(unsigned char id)
    {
        if (id < ID_USER_PACKET_ENUM)
            return kMessageNames[id];
        return "ID_USER_PACKET_ENUM+" + std::to_string(id - ID_USER_PACKET_ENUM);
    }

    std::string StartupResultName(RakNet::StartupResult result)
    {
        switch (result)
        {
        case RakNet::RAKNET_STARTED: return "RAKNET_STARTED";
        case RakNet::RAKNET_ALREADY_STARTED: return "RAKNET_ALREADY_STARTED";
        case RakNet::INVALID_SOCKET_DESCRIPTORS: return "INVALID_SOCKET_DESCRIPTORS";
        case RakNet::INVALID_MAX_CONNECTIONS: return "INVALID_MAX_CONNECTIONS";
        case RakNet::SOCKET_FAMILY_NOT_SUPPORTED: return "SOCKET_FAMILY_NOT_SUPPORTED";
        case RakNet::SOCKET_PORT_ALREADY_IN_USE: return "SOCKET_PORT_ALREADY_IN_USE";
        case RakNet::SOCKET_FAILED_TO_BIND: return "SOCKET_FAILED_TO_BIND";
        case RakNet::SOCKET_FAILED_TEST_SEND: return "SOCKET_FAILED_TEST_SEND";
        case RakNet::PORT_CANNOT_BE_ZERO: return "PORT_CANNOT_BE_ZERO";
        case RakNet::FAILED_TO_CREATE_NETWORK_THREAD: return "FAILED_TO_CREATE_NETWORK_THREAD";
        case RakNet::COULD_NOT_GENERATE_GUID: return "COULD_NOT_GENERATE_GUID";
        case RakNet::STARTUP_OTHER_FAILURE: return "STARTUP_OTHER_FAILURE";
        }
        return "StartupResult(" + std::to_string(static_cast<int>(result)) + ")";
    }

    std::string ConnectionAttemptResultName(RakNet::ConnectionAttemptResult result)
    {
        switch (result)
        {
        case RakNet::CONNECTION_ATTEMPT_STARTED: return "CONNECTION_ATTEMPT_STARTED";
        case RakNet::INVALID_PARAMETER: return "INVALID_PARAMETER";
        case RakNet::CANNOT_RESOLVE_DOMAIN_NAME: return "CANNOT_RESOLVE_DOMAIN_NAME";
        case RakNet::ALREADY_CONNECTED_TO_ENDPOINT: return "ALREADY_CONNECTED_TO_ENDPOINT";
        case RakNet::CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS: return "CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS";
        case RakNet::SECURITY_INITIALIZATION_FAILED: return "SECURITY_INITIALIZATION_FAILED";
        }
        return "ConnectionAttemptResult(" + std::to_string(static_cast<int>(result)) + ")";
    }

    std::string ConnectionStateName(RakNet::ConnectionState state)
    {
        switch (state)
        {
        case RakNet::IS_PENDING: return "IS_PENDING";
        case RakNet::IS_CONNECTING: return "IS_CONNECTING";
        case RakNet::IS_CONNECTED: return "IS_CONNECTED";
        case RakNet::IS_DISCONNECTING: return "IS_DISCONNECTING";
        case RakNet::IS_SILENTLY_DISCONNECTING: return "IS_SILENTLY_DISCONNECTING";
        case RakNet::IS_DISCONNECTED: return "IS_DISCONNECTED";
        case RakNet::IS_NOT_CONNECTED: return "IS_NOT_CONNECTED";
        }
        return "ConnectionState(" + std::to_string(static_cast<int>(state)) + ")";
    }

    std::string PerSecondMetricName(int metric)
    {
        switch (metric)
        {
        case RakNet::USER_MESSAGE_BYTES_PUSHED: return "USER_MESSAGE_BYTES_PUSHED";
        case RakNet::USER_MESSAGE_BYTES_SENT: return "USER_MESSAGE_BYTES_SENT";
        case RakNet::USER_MESSAGE_BYTES_RESENT: return "USER_MESSAGE_BYTES_RESENT";
        case RakNet::USER_MESSAGE_BYTES_RECEIVED_PROCESSED: return "USER_MESSAGE_BYTES_RECEIVED_PROCESSED";
        case RakNet::USER_MESSAGE_BYTES_RECEIVED_IGNORED: return "USER_MESSAGE_BYTES_RECEIVED_IGNORED";
        case RakNet::ACTUAL_BYTES_SENT: return "ACTUAL_BYTES_SENT";
        case RakNet::ACTUAL_BYTES_RECEIVED: return "ACTUAL_BYTES_RECEIVED";
        }
        return "RNSPerSecondMetrics(" + std::to_string(metric) + ")";
    }

    bool TryParseReliability(const std::string& name, PacketReliability& value)
    {
        for (const ReliabilityName& entry : kReliabilities)
        {
            if (name == entry.name)
            {
                value = entry.value;
                return true;
            }
        }
        return false;
    }

    bool TryParsePriority(const std::string& name, PacketPriority& value)
    {
        for (const PriorityName& entry : kPriorities)
        {
            if (name == entry.name)
            {
                value = entry.value;
                return true;
            }
        }
        return false;
    }
}
