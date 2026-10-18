#pragma once

#include "PacketPriority.h"
#include "RakNetTypes.h"

#include <string>

namespace interop
{
    // C++ DefaultMessageIDTypes name for IDs below ID_USER_PACKET_ENUM,
    // otherwise "ID_USER_PACKET_ENUM+<n>".
    std::string MessageName(unsigned char id);

    std::string StartupResultName(RakNet::StartupResult result);
    std::string ConnectionAttemptResultName(RakNet::ConnectionAttemptResult result);
    std::string ConnectionStateName(RakNet::ConnectionState state);
    std::string PerSecondMetricName(int metric);

    bool TryParseReliability(const std::string& name, PacketReliability& value);
    bool TryParsePriority(const std::string& name, PacketPriority& value);
}
