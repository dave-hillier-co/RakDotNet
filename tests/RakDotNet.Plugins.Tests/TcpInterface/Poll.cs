using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TcpInterface;

/// <summary>
/// Waits on a <see cref="Plugins.TcpInterface"/> the way RakNet's samples do, by calling its polling members until they
/// report something. The interface does its I/O on its own tasks over real loopback sockets, so a test can only wait.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static T Until<T>(Func<T?> poll, string what) where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (poll() is { } value)
                return value;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(2);
        }
    }

    public static SystemAddress Address(Func<SystemAddress> poll, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var address = poll();
            if (address != SystemAddress.Unassigned)
                return address;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(2);
        }
    }

    public static void True(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(2);
        }
    }

    /// <summary>Receives until <paramref name="count"/> bytes from <paramref name="from"/> have arrived; TCP may split or join sends.</summary>
    public static byte[] Bytes(Plugins.TcpInterface tcp, SystemAddress from, int count)
    {
        var received = new List<byte>();
        True(() =>
        {
            while (tcp.Receive() is { } packet)
            {
                Assert.Equal(from, packet.Address);
                received.AddRange(packet.Data.ToArray());
            }
            return received.Count >= count;
        }, $"{count} bytes from {from}");
        return [.. received];
    }

    /// <summary>Calls <paramref name="poll"/> for a while and asserts it never reports anything.</summary>
    public static void Never(Func<bool> reported, string what, int milliseconds = 300)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(reported(), $"Unexpected {what}.");
            Thread.Sleep(5);
        }
    }
}
