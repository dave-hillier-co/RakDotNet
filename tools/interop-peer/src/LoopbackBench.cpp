// loopback-bench: RakNet's Samples/LoopbackPerformanceTest reduced to one process and a fixed burst, so it can be
// run unattended and compared with `RakDotNet.Benchmarks loopback`, which takes the same arguments and prints the same
// result line (docs/performance.md).
//
// A client and a server RakPeerInterface connect over 127.0.0.1. Each round the client sends --messages copies of a
// --bytes message (ID_USER_PACKET_ENUM then zeros) with --reliability at HIGH_PRIORITY on channel 0, as the sample's
// source does, and the round ends when the server has received the last one.

#include "Names.h"

#include "MessageIdentifiers.h"
#include "RakNetTypes.h"
#include "RakPeerInterface.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
    constexpr const char* kUsage =
        "usage: loopback-bench [--messages N] [--bytes N] [--reliability NAME] [--rounds N]\n";

    struct Options
    {
        unsigned messages = 100000;
        unsigned bytes = 400;
        unsigned rounds = 3;
        PacketReliability reliability = RELIABLE_ORDERED;
        std::string reliabilityName = "RELIABLE_ORDERED";
    };

    bool ParsePositive(const char* text, unsigned& value)
    {
        char* end = nullptr;
        unsigned long parsed = std::strtoul(text, &end, 10);
        if (end == text || *end != '\0' || parsed == 0 || parsed > 100000000UL)
            return false;
        value = static_cast<unsigned>(parsed);
        return true;
    }

    bool Parse(int argc, char** argv, Options& options)
    {
        for (int i = 1; i < argc; i += 2)
        {
            if (i + 1 >= argc)
                return false;
            std::string name = argv[i];
            const char* value = argv[i + 1];
            if (name == "--messages" && ParsePositive(value, options.messages))
                continue;
            if (name == "--bytes" && ParsePositive(value, options.bytes))
                continue;
            if (name == "--rounds" && ParsePositive(value, options.rounds))
                continue;
            if (name == "--reliability" && interop::TryParseReliability(value, options.reliability))
            {
                options.reliabilityName = value;
                continue;
            }
            return false;
        }
        return true;
    }

    RakNet::RakPeerInterface* StartPeer(unsigned short maxIncoming)
    {
        RakNet::RakPeerInterface* peer = RakNet::RakPeerInterface::GetInstance();
        RakNet::SocketDescriptor socket(0, "127.0.0.1");
        if (peer->Startup(1, &socket, 1) != RakNet::RAKNET_STARTED)
            throw std::runtime_error("Startup failed");
        peer->SetMaximumIncomingConnections(maxIncoming);
        return peer;
    }

    // Reads every waiting packet; returns how many carried ID_USER_PACKET_ENUM and fails on a lost connection.
    unsigned Drain(RakNet::RakPeerInterface* peer, bool& connected)
    {
        unsigned messages = 0;
        for (RakNet::Packet* packet = peer->Receive(); packet != nullptr; packet = peer->Receive())
        {
            switch (packet->data[0])
            {
            case ID_USER_PACKET_ENUM:
                messages++;
                break;
            case ID_CONNECTION_REQUEST_ACCEPTED:
            case ID_NEW_INCOMING_CONNECTION:
                connected = true;
                break;
            case ID_DISCONNECTION_NOTIFICATION:
            case ID_CONNECTION_LOST:
            case ID_CONNECTION_ATTEMPT_FAILED:
                peer->DeallocatePacket(packet);
                throw std::runtime_error("connection lost");
            default:
                break;
            }
            peer->DeallocatePacket(packet);
        }
        return messages;
    }
}

int main(int argc, char** argv)
{
    Options options;
    if (!Parse(argc, argv, options))
    {
        std::fputs(kUsage, stderr);
        return 2;
    }

    try
    {
        RakNet::RakPeerInterface* server = StartPeer(1);
        RakNet::RakPeerInterface* client = StartPeer(0);
        unsigned short serverPort = server->GetMyBoundAddress().GetPort();
        if (client->Connect("127.0.0.1", serverPort, nullptr, 0) != RakNet::CONNECTION_ATTEMPT_STARTED)
            throw std::runtime_error("Connect failed");

        bool clientConnected = false;
        bool serverConnected = false;
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (!clientConnected || !serverConnected)
        {
            if (std::chrono::steady_clock::now() > deadline)
                throw std::runtime_error("the client and server did not connect");
            Drain(client, clientConnected);
            Drain(server, serverConnected);
            std::this_thread::yield();
        }

        std::vector<char> message(options.bytes, 0);
        message[0] = static_cast<char>(ID_USER_PACKET_ENUM);
        RakNet::AddressOrGUID target(server->GetMyGUID());
        for (unsigned round = 1; round <= options.rounds; round++)
        {
            auto start = std::chrono::steady_clock::now();
            for (unsigned i = 0; i < options.messages; i++)
                client->Send(message.data(), static_cast<int>(message.size()), HIGH_PRIORITY, options.reliability, 0, target, false);

            unsigned received = 0;
            while (received < options.messages)
            {
                unsigned got = Drain(server, serverConnected);
                Drain(client, clientConnected);
                received += got;
                if (got == 0)
                    std::this_thread::yield();
            }
            double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
            std::printf(
                "RakNet loopback round=%u messages=%u bytes=%u reliability=%s seconds=%.3f messages/s=%.0f MB/s=%.1f\n",
                round, options.messages, options.bytes, options.reliabilityName.c_str(), seconds,
                options.messages / seconds, options.messages * static_cast<double>(options.bytes) / seconds / 1e6);
            std::fflush(stdout);
        }

        client->Shutdown(100);
        server->Shutdown(100);
        RakNet::RakPeerInterface::DestroyInstance(client);
        RakNet::RakPeerInterface::DestroyInstance(server);
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "loopback-bench: %s\n", error.what());
        return 1;
    }
    return 0;
}
