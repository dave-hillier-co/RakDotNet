// Replaces sendto and recvfrom for the whole process, so RakNet's sockets, and the sockets its NAT plugins open, can be
// put behind a NAT router of the test harness without patching RakNet. RakNet calls sendto__ and recvfrom__, which
// SocketDefines.h maps to sendto and recvfrom; this object is linked before RakNet's library, so the linker binds those
// calls here, and the system's functions are found with dlsym(RTLD_NEXT).

// glibc's fortified recvfrom is an inline wrapper that would call __recvfrom_chk around this definition.
#undef _FORTIFY_SOURCE

#include "NatGateway.h"

#include <arpa/inet.h>
#include <dlfcn.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/types.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cstring>

namespace
{
    using SendTo = ssize_t (*)(int, const void*, size_t, int, const struct sockaddr*, socklen_t);
    using RecvFrom = ssize_t (*)(int, void*, size_t, int, struct sockaddr*, socklen_t*);

    // The gateway's IPv4 address and port in network order, as (address << 16) | port; 0 when there is none.
    std::atomic<std::uint64_t> gateway{0};

    // The largest UDP payload, plus the header.
    constexpr size_t kMaxDatagram = 65536 + interop::kNatGatewayHeaderSize;

    SendTo RealSendTo()
    {
        static SendTo real = reinterpret_cast<SendTo>(dlsym(RTLD_NEXT, "sendto"));
        return real;
    }

    RecvFrom RealRecvFrom()
    {
        static RecvFrom real = reinterpret_cast<RecvFrom>(dlsym(RTLD_NEXT, "recvfrom"));
        return real;
    }

    sockaddr_in GatewayAddress(std::uint64_t packed)
    {
        sockaddr_in address;
        std::memset(&address, 0, sizeof(address));
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = static_cast<std::uint32_t>(packed >> 16);
        address.sin_port = static_cast<std::uint16_t>(packed & 0xFFFF);
        return address;
    }

    // The socket's own address: RakNet sends to it to unblock its receive thread and to test a new socket.
    bool IsOwnAddress(int fd, const sockaddr_in& address)
    {
        sockaddr_in bound;
        socklen_t length = sizeof(bound);
        if (getsockname(fd, reinterpret_cast<sockaddr*>(&bound), &length) != 0 || bound.sin_family != AF_INET)
            return false;
        return bound.sin_port == address.sin_port
            && (bound.sin_addr.s_addr == htonl(INADDR_ANY) || bound.sin_addr.s_addr == address.sin_addr.s_addr);
    }

    bool IsGateway(const sockaddr_in& address, std::uint64_t packed)
    {
        const sockaddr_in gatewayAddress = GatewayAddress(packed);
        return address.sin_family == AF_INET && address.sin_addr.s_addr == gatewayAddress.sin_addr.s_addr
            && address.sin_port == gatewayAddress.sin_port;
    }

    void Report(const sockaddr_in& source, struct sockaddr* from, socklen_t* fromLength)
    {
        if (from == nullptr || fromLength == nullptr)
            return;
        const socklen_t length = std::min<socklen_t>(*fromLength, sizeof(source));
        std::memcpy(from, &source, length);
        *fromLength = sizeof(source);
    }
}

namespace interop
{
    void SetNatGateway(std::uint32_t ipv4NetworkOrder, std::uint16_t portNetworkOrder)
    {
        gateway = (static_cast<std::uint64_t>(ipv4NetworkOrder) << 16) | portNetworkOrder;
    }

    void ClearNatGateway()
    {
        gateway = 0;
    }
}

extern "C" ssize_t sendto(int fd, const void* buffer, size_t length, int flags, const struct sockaddr* to, socklen_t toLength)
{
    const std::uint64_t packed = gateway.load();
    if (packed == 0 || to == nullptr || to->sa_family != AF_INET || toLength < static_cast<socklen_t>(sizeof(sockaddr_in))
        || length + interop::kNatGatewayHeaderSize > kMaxDatagram)
        return RealSendTo()(fd, buffer, length, flags, to, toLength);
    sockaddr_in destination;
    std::memcpy(&destination, to, sizeof(destination));
    if (IsOwnAddress(fd, destination))
        return RealSendTo()(fd, buffer, length, flags, to, toLength);

    // SendTTL sets IP_TTL around its send (RakNetSocket2_Windows_Linux_360.cpp:29-61); the router needs to see it.
    int ttl = 0;
    socklen_t ttlLength = sizeof(ttl);
    if (getsockopt(fd, IPPROTO_IP, IP_TTL, &ttl, &ttlLength) != 0)
        ttl = 0;

    static thread_local unsigned char datagram[kMaxDatagram];
    datagram[0] = static_cast<unsigned char>(std::clamp(ttl, 0, 255));
    std::memcpy(datagram + 1, &destination.sin_addr.s_addr, 4);
    std::memcpy(datagram + 5, &destination.sin_port, 2);
    std::memcpy(datagram + interop::kNatGatewayHeaderSize, buffer, length);
    const sockaddr_in gatewayAddress = GatewayAddress(packed);
    const ssize_t sent = RealSendTo()(fd, datagram, length + interop::kNatGatewayHeaderSize, flags,
                                      reinterpret_cast<const sockaddr*>(&gatewayAddress), sizeof(gatewayAddress));
    return sent < 0 ? sent : std::max<ssize_t>(0, sent - interop::kNatGatewayHeaderSize);
}

extern "C" ssize_t recvfrom(int fd, void* buffer, size_t length, int flags, struct sockaddr* from, socklen_t* fromLength)
{
    if (gateway.load() == 0)
        return RealRecvFrom()(fd, buffer, length, flags, from, fromLength);

    static thread_local unsigned char datagram[kMaxDatagram];
    for (;;)
    {
        sockaddr_in source;
        socklen_t sourceLength = sizeof(source);
        const ssize_t received = RealRecvFrom()(fd, datagram, sizeof(datagram), flags, reinterpret_cast<sockaddr*>(&source), &sourceLength);
        if (received < 0)
            return received;
        const std::uint64_t packed = gateway.load();
        if (packed == 0 || IsOwnAddress(fd, source))
        {
            const size_t copied = std::min(static_cast<size_t>(received), length);
            std::memcpy(buffer, datagram, copied);
            Report(source, from, fromLength);
            return static_cast<ssize_t>(copied);
        }
        if (!IsGateway(source, packed) || received < interop::kNatGatewayHeaderSize)
            continue; // Nothing outside routes to a host behind a NAT.

        sockaddr_in remote;
        std::memset(&remote, 0, sizeof(remote));
        remote.sin_family = AF_INET;
        std::memcpy(&remote.sin_addr.s_addr, datagram + 1, 4);
        std::memcpy(&remote.sin_port, datagram + 5, 2);
        const size_t payload = static_cast<size_t>(received) - interop::kNatGatewayHeaderSize;
        const size_t copied = std::min(payload, length);
        std::memcpy(buffer, datagram + interop::kNatGatewayHeaderSize, copied);
        Report(remote, from, fromLength);
        return static_cast<ssize_t>(copied);
    }
}

#if defined(__linux__)
// glibc's fortified callers reach recvfrom through __recvfrom_chk.
extern "C" ssize_t __recvfrom_chk(int fd, void* buffer, size_t length, size_t bufferLength, int flags, struct sockaddr* from, socklen_t* fromLength)
{
    (void)bufferLength;
    return recvfrom(fd, buffer, length, flags, from, fromLength);
}
#endif
