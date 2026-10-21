using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// NetworkIDObject: an object other systems can refer to by its <see cref="NetworkId"/>. Derive from it, or hold one as a
/// member and set <see cref="Parent"/> to the owner.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NetworkIDObject.cpp. One system, the authority, gives each object a <see cref="Manager"/>, which assigns a
/// new ID, and sends that ID to the others; they create the same kind of object and set <see cref="NetworkId"/> to it,
/// before or after setting the manager. <see cref="Dispose()"/> stops the manager tracking the object, as the C++
/// destructor does.
/// </para>
/// <para>Not thread-safe, as in RakNet.</para>
/// </remarks>
public class NetworkIdObject : IDisposable
{
    private NetworkIdManager? _manager;
    private ulong _networkId = RakNetConstants.UnassignedNetworkId;

    /// <summary>
    /// GetNetworkIDManager and SetNetworkIDManager: the manager tracking this object. Setting one gives an object without
    /// an ID a new ID; setting null unassigns the ID.
    /// </summary>
    public NetworkIdManager? Manager
    {
        get => _manager;
        set
        {
            // NetworkIDObject.cpp:32-53.
            if (ReferenceEquals(value, _manager))
                return;
            _manager?.StopTracking(this);
            _manager = value;
            if (value is null)
            {
                _networkId = RakNetConstants.UnassignedNetworkId;
                return;
            }
            if (_networkId == RakNetConstants.UnassignedNetworkId)
                _networkId = value.NewNetworkId();
            value.Track(this);
        }
    }

    /// <summary>
    /// GetNetworkID and SetNetworkID: the ID other systems use for this object, or
    /// <see cref="RakNetConstants.UnassignedNetworkId"/>. Setting <see cref="RakNetConstants.UnassignedNetworkId"/>
    /// removes the manager.
    /// </summary>
    public ulong NetworkId
    {
        get => _networkId;
        set
        {
            // NetworkIDObject.cpp:62-80.
            if (_networkId == value)
                return;
            if (value == RakNetConstants.UnassignedNetworkId)
            {
                Manager = null;
                return;
            }
            _manager?.StopTracking(this);
            _networkId = value;
            _manager?.Track(this);
        }
    }

    /// <summary>
    /// GetParent and SetParent: what <see cref="NetworkIdManager.GetObject{T}"/> returns for this object's ID instead of
    /// the object, when this object is a member of the parent rather than its base.
    /// </summary>
    public object? Parent { get; set; }

    // nextInstanceForNetworkIDManager: the next object in this one's manager bucket.
    internal NetworkIdObject? NextInBucket { get; set; }

    /// <summary>The destructor: stops the manager tracking this object.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the manager tracking this object; a derived class releasing its own resources overrides it.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _manager?.StopTracking(this);
    }
}
