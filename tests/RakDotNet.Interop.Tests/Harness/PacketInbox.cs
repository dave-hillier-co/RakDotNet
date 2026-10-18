using System.Threading.Channels;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Buffers a peer's packet stream so scenarios can wait for specific packets in any order. Packets that do not
/// match a wait stay in the inbox for later waits. Failures say what was expected and what arrived instead.
/// </summary>
public sealed class PacketInbox(ChannelReader<ReceivedPacket> packets, string label) : IDisposable
{
    private readonly List<ReceivedPacket> _unconsumed = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Label { get; } = label;

    public IReadOnlyList<ReceivedPacket> Unconsumed
    {
        get
        {
            lock (_unconsumed)
            {
                return _unconsumed.ToArray();
            }
        }
    }

    public async Task<ReceivedPacket> WaitForPacketAsync(Func<ReceivedPacket, bool> predicate, string expectation, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        (await WaitForPacketsAsync(predicate, 1, expectation, timeout, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Waits until <paramref name="count"/> matching packets have arrived and removes them, oldest first.</summary>
    public async Task<IReadOnlyList<ReceivedPacket>> WaitForPacketsAsync(
        Func<ReceivedPacket, bool> predicate,
        int count,
        string expectation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var found = new List<ReceivedPacket>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_unconsumed)
            {
                for (var i = 0; i < _unconsumed.Count && found.Count < count; i++)
                {
                    if (predicate(_unconsumed[i]))
                    {
                        found.Add(_unconsumed[i]);
                        _unconsumed.RemoveAt(i--);
                    }
                }
            }

            while (found.Count < count)
            {
                ReceivedPacket packet;
                try
                {
                    if (!await packets.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
                    {
                        Restore(found);
                        throw new InvalidOperationException(
                            $"{Label} packet stream ended (the peer exited) while waiting for {expectation}; {found.Count} of {count} arrived. {Describe()}");
                    }
                    if (!packets.TryRead(out packet!))
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var arrived = found.Count;
                    Restore(found);
                    throw new TimeoutException(
                        $"{Label} did not receive {expectation} within {timeout.TotalSeconds:0.###} s; {arrived} of {count} arrived. {Describe()}");
                }

                if (predicate(packet))
                {
                    found.Add(packet);
                }
                else
                {
                    lock (_unconsumed)
                    {
                        _unconsumed.Add(packet);
                    }
                }
            }

            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    // On failure the matched packets go back, so the description below shows everything that arrived.
    private void Restore(List<ReceivedPacket> found)
    {
        lock (_unconsumed)
        {
            _unconsumed.InsertRange(0, found);
        }
    }

    public void Dispose() => _gate.Dispose();

    private string Describe()
    {
        var unconsumed = Unconsumed;
        if (unconsumed.Count == 0)
        {
            return "No other packets arrived.";
        }

        var names = unconsumed.Take(20).Select(p => $"{p.MessageName} ({p.Data.Length} bytes from {p.Address})");
        var more = unconsumed.Count > 20 ? $" and {unconsumed.Count - 20} more" : "";
        return $"Packets received but not matched: {string.Join(", ", names)}{more}.";
    }
}
