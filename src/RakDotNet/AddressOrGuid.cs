using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// RakNet's AddressOrGUID: a send or close target. Holds exactly one of a GUID or an address, never both, so a target
/// is never ambiguous and RakNet's GUID-before-address precedence (RakNetTypes.cpp:56-70) never applies.
/// </summary>
/// <remarks>
/// <c>default</c> is undefined, which is what <c>except: default</c> on a broadcast means. Converting an unassigned GUID
/// or address gives an undefined target.
/// </remarks>
public readonly struct AddressOrGuid : IEquatable<AddressOrGuid>
{
    private readonly RakNetGuid _guid;
    private readonly SystemAddress _address;
    private readonly Kind _kind;

    private AddressOrGuid(RakNetGuid rakNetGuid)
    {
        _guid = rakNetGuid;
        _kind = rakNetGuid.IsAssigned ? Kind.Guid : Kind.Undefined;
    }

    private AddressOrGuid(SystemAddress address)
    {
        _address = address;
        _kind = address.IsAssigned ? Kind.Address : Kind.Undefined;
    }

    private enum Kind : byte
    {
        Undefined,
        Guid,
        Address,
    }

    /// <summary>The GUID, or <see cref="RakNetGuid.Unassigned"/> when this holds an address.</summary>
    public RakNetGuid RakNetGuid => _kind == Kind.Guid ? _guid : RakNetGuid.Unassigned;

    /// <summary>The address, or <see cref="SystemAddress.Unassigned"/> when this holds a GUID.</summary>
    public SystemAddress Address => _kind == Kind.Address ? _address : SystemAddress.Unassigned;

    /// <summary>AddressOrGUID::IsUndefined (RakNetTypes.h:378).</summary>
    public bool IsUndefined => _kind == Kind.Undefined;

    public static implicit operator AddressOrGuid(RakNetGuid rakNetGuid) => new(rakNetGuid);

    public static implicit operator AddressOrGuid(SystemAddress address) => new(address);

    public static implicit operator AddressOrGuid(IPEndPoint endPoint) => new((SystemAddress)endPoint);

    /// <summary>The connection's <see cref="RakConnection.RakNetGuid"/>.</summary>
    public static implicit operator AddressOrGuid(RakConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new(connection.RakNetGuid);
    }

    public bool Equals(AddressOrGuid other) => _kind == other._kind && RakNetGuid == other.RakNetGuid && Address == other.Address;

    public override bool Equals(object? obj) => obj is AddressOrGuid other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_kind, RakNetGuid, Address);

    public static bool operator ==(AddressOrGuid left, AddressOrGuid right) => left.Equals(right);

    public static bool operator !=(AddressOrGuid left, AddressOrGuid right) => !left.Equals(right);

    public override string ToString() => _kind switch
    {
        Kind.Guid => _guid.ToString(),
        Kind.Address => _address.ToString(),
        _ => "UNDEFINED",
    };
}
