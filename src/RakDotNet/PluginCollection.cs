using System.Collections;

namespace RakDotNet;

/// <summary>
/// <see cref="RakPeer.Plugins"/>: AttachPlugin / DetachPlugin. Copy-on-write arrays, so dispatch allocates nothing.
/// </summary>
/// <remarks>
/// Attaching or detaching from a plugin hook acts inside the current dispatch step; from anywhere else, at the start of
/// the next one. Attaching a plugin already attached elsewhere throws <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class PluginCollection : IReadOnlyCollection<IRakPlugin>
{
    private PluginCollection() { }

    public int Count => throw new NotImplementedException();

    public void Attach(IRakPlugin plugin) => throw new NotImplementedException();

    public bool Detach(IRakPlugin plugin) => throw new NotImplementedException();

    public IEnumerator<IRakPlugin> GetEnumerator() => throw new NotImplementedException();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
