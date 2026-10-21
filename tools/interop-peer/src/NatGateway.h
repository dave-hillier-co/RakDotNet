#pragma once

#include <cstdint>

namespace interop
{
    // Puts this process behind a NAT router of the test harness (tests/RakDotNet.Interop.Tests/Harness/NatEmulator.cs).
    // While a gateway is set, every UDP datagram the process sends to another address goes to the gateway instead,
    // after a 7-byte header: the IP time-to-live the socket would send with (0 for the default), then the destination's
    // IPv4 address and port in network order. Datagrams from the gateway carry the same header naming their source, which
    // recvfrom reports in its place; anything else that arrives is dropped, as nothing outside routes to a host behind a
    // NAT. A socket's datagrams to its own address pass untouched, as RakNet sends them to unblock its receive thread.
    // Specified in docs/interop-protocol.md.
    //
    // The process's sendto and recvfrom are replaced (NatGateway.cpp); with no gateway they call the system's.
    void SetNatGateway(std::uint32_t ipv4NetworkOrder, std::uint16_t portNetworkOrder);
    void ClearNatGateway();

    constexpr int kNatGatewayHeaderSize = 7;
}
