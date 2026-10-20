using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Tests;

/// <summary>Whether this host can send to 255.255.255.255: it needs an interface with a broadcast route.</summary>
internal static class Broadcast
{
    public static void SkipUnlessAvailable()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        bool sent;
        try
        {
            sent = socket.SendTo([0], new IPEndPoint(IPAddress.Broadcast, ((IPEndPoint)socket.LocalEndPoint!).Port)) == 1;
        }
        catch (SocketException)
        {
            sent = false;
        }
        Assert.SkipUnless(sent, "This host has no broadcast route.");
    }
}
