using System.Collections;
using System.Runtime.CompilerServices;

namespace RakDotNet;

/// <summary>
/// <see cref="RakPeer.Plugins"/>: AttachPlugin / DetachPlugin. Copy-on-write arrays, so dispatch allocates nothing.
/// </summary>
/// <remarks>
/// Attaching or detaching from a plugin hook acts inside the current dispatch step; from anywhere else, at the start of
/// the next one. Attaching a plugin already attached elsewhere throws <see cref="InvalidOperationException"/>. The
/// collection lists the plugins whose attach has been applied: RakNet's pluginListTS, then pluginListNTS.
/// </remarks>
public sealed class PluginCollection : IReadOnlyCollection<IRakPlugin>
{
    private static readonly ConditionalWeakTable<IRakPlugin, RakPeer> Owners = new();
    private static readonly Lock OwnersGate = new();

    private readonly RakPeer _peer;
    private volatile IRakPlugin[] _ordinary = [];
    private volatile IRakPlugin[] _reliabilityLayer = [];
    private volatile INetworkObserver[] _observers = [];

    internal PluginCollection(RakPeer peer) => _peer = peer;

    public int Count => _ordinary.Length + _reliabilityLayer.Length;

    /// <summary>pluginListTS: plugins that do not use the reliability layer, in attach order.</summary>
    internal IRakPlugin[] Ordinary => _ordinary;

    /// <summary>pluginListNTS: plugins that also implement <see cref="INetworkObserver"/>.</summary>
    internal IRakPlugin[] ReliabilityLayer => _reliabilityLayer;

    internal INetworkObserver[] Observers => _observers;

    public void Attach(IRakPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _peer.ThrowIfStopped();
        if (Claim(plugin))
            _peer.RequestAttach(plugin);
    }

    /// <summary>A plugin from <see cref="RakPeerOptions.Plugins"/>: attached at once, on the caller of Start.</summary>
    internal void AttachAtStart(IRakPlugin plugin)
    {
        if (Claim(plugin))
            Apply(plugin);
    }

    private bool Claim(IRakPlugin plugin)
    {
        lock (OwnersGate)
        {
            if (Owners.TryGetValue(plugin, out var owner))
            {
                if (!ReferenceEquals(owner, _peer))
                    throw new InvalidOperationException("The plugin is attached to another peer.");
                return false;
            }
            Owners.Add(plugin, _peer);
            return true;
        }
    }

    public bool Detach(IRakPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (OwnersGate)
        {
            if (!Owners.TryGetValue(plugin, out var owner) || !ReferenceEquals(owner, _peer))
                return false;
        }
        _peer.RequestDetach(plugin);
        return true;
    }

    public IEnumerator<IRakPlugin> GetEnumerator()
    {
        foreach (var plugin in _ordinary)
            yield return plugin;
        foreach (var plugin in _reliabilityLayer)
            yield return plugin;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>RakPeer::AttachPlugin (RakPeer.cpp:2760-2781). Called on the dispatch step or during start.</summary>
    internal void Apply(IRakPlugin plugin)
    {
        if (Array.IndexOf(_ordinary, plugin) >= 0 || Array.IndexOf(_reliabilityLayer, plugin) >= 0)
            return;
        plugin.OnAttached(_peer);
        if (plugin is INetworkObserver observer)
        {
            _reliabilityLayer = [.. _reliabilityLayer, plugin];
            _observers = [.. _observers, observer];
        }
        else
        {
            _ordinary = [.. _ordinary, plugin];
        }
    }

    /// <summary>RakPeer::DetachPlugin (RakPeer.cpp:2788-2818): the last plugin takes the removed one's place.</summary>
    internal void Remove(IRakPlugin plugin)
    {
        if (plugin is INetworkObserver observer)
        {
            _reliabilityLayer = RemoveSwappingLast(_reliabilityLayer, plugin);
            _observers = RemoveSwappingLast(_observers, observer);
        }
        else
        {
            _ordinary = RemoveSwappingLast(_ordinary, plugin);
        }
        plugin.OnDetached();
        Release(plugin);
    }

    internal void ReleaseAll()
    {
        foreach (var plugin in this)
            Release(plugin);
    }

    private void Release(IRakPlugin plugin)
    {
        lock (OwnersGate)
        {
            if (Owners.TryGetValue(plugin, out var owner) && ReferenceEquals(owner, _peer))
                Owners.Remove(plugin);
        }
    }

    private static T[] RemoveSwappingLast<T>(T[] items, T item)
        where T : class
    {
        var index = Array.IndexOf(items, item);
        if (index < 0)
            return items;
        var copy = items[..^1];
        if (index < copy.Length)
            copy[index] = items[^1];
        return copy;
    }
}
