using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The randSync*, rakNetRandom, networkId*, grid* and delta* commands on <see cref="RakNetRandomSync"/>,
/// <see cref="RakNetRandom"/>, <see cref="NetworkIdManager"/>, <see cref="GridSectorizer{T}"/> and
/// <see cref="VariableDeltaSerializer"/>, with interop-peer's results and errors. Called from one test at a time.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IRandSyncDriver, INetworkIdDriver, IGridSectorizerDriver, IDeltaSerializerDriver
{
    private readonly RakNetRandomSync _randSync = new();
    private readonly Dictionary<int, HandleObject> _networkIdObjects = [];
    private NetworkIdManager? _networkIdManager;
    private GridSectorizer<int>? _grid;
    private VariableDeltaSerializer? _deltaSerializer;

    public Task<RakNetRandomOutput> DrawRakNetRandomAsync(uint seed, int count, int floatCount = 0, int? fillBytes = null, CancellationToken cancellationToken = default)
    {
        var random = new RakNetRandom();
        random.Seed(seed);
        uint[] values = [.. Enumerable.Range(0, count).Select(_ => random.NextUInt32())];
        uint[] floats = [.. Enumerable.Range(0, floatCount).Select(_ => BitConverter.SingleToUInt32Bits(random.NextSingle()))];
        byte[]? fill = null;
        uint? afterFill = null;
        if (fillBytes is { } bytes)
        {
            fill = new byte[bytes];
            random.FillBuffer(fill);
            afterFill = random.NextUInt32();
        }
        return Task.FromResult(new RakNetRandomOutput(values, floats, fill, afterFill));
    }

    public Task SeedRandSyncAsync(uint seed, uint? skip = null, CancellationToken cancellationToken = default)
    {
        _randSync.Seed(seed, skip ?? 0);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<uint>> NextRandSyncAsync(int count, bool asFloat = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<uint>>([.. Enumerable.Range(0, count).Select(_ => asFloat ? BitConverter.SingleToUInt32Bits(_randSync.NextSingle()) : _randSync.NextUInt32())]);

    public Task<byte[]> SerializeRandSyncConstructionAsync(CancellationToken cancellationToken = default)
    {
        using var stream = new BitStream();
        _randSync.SerializeConstruction(stream);
        return Task.FromResult(stream.Written.ToArray());
    }

    public Task<bool> DeserializeRandSyncConstructionAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var reader = new BitStreamReader(data.Span);
        return Task.FromResult(_randSync.DeserializeConstruction(ref reader));
    }

    public Task<byte[]> SerializeRandSyncAsync(CancellationToken cancellationToken = default)
    {
        using var stream = new BitStream();
        _randSync.Serialize(stream);
        return Task.FromResult(stream.Written.ToArray());
    }

    public Task DeserializeRandSyncAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length < sizeof(uint))
            throw new PeerCommandException("randSyncDeserialize", "field 'data' is shorter than a call count");
        var reader = new BitStreamReader(data.Span);
        _randSync.Deserialize(ref reader);
        return Task.CompletedTask;
    }

    public Task<(uint Seed, uint CallCount)> GetRandSyncInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((_randSync.CurrentSeed, _randSync.CallCount));

    public Task NewNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default)
    {
        if (!_networkIdObjects.TryAdd(handle, new HandleObject(handle)))
            throw new PeerCommandException("networkIdObjectNew", $"an object with handle {handle} exists");
        return Task.CompletedTask;
    }

    public Task SetNetworkIdManagerAsync(int handle, bool attach = true, CancellationToken cancellationToken = default)
    {
        NetworkIdObjectOf("networkIdObjectSetManager", handle).Manager = attach ? NetworkIds : null;
        return Task.CompletedTask;
    }

    public Task SetNetworkIdAsync(int handle, ulong networkId, CancellationToken cancellationToken = default)
    {
        NetworkIdObjectOf("networkIdObjectSetId", handle).NetworkId = networkId;
        return Task.CompletedTask;
    }

    public Task SetNetworkIdParentAsync(int handle, int? parent, CancellationToken cancellationToken = default)
    {
        NetworkIdObjectOf("networkIdObjectSetParent", handle).Parent = parent is { } p ? NetworkIdObjectOf("networkIdObjectSetParent", p) : null;
        return Task.CompletedTask;
    }

    public Task DeleteNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default)
    {
        NetworkIdObjectOf("networkIdObjectDelete", handle).Dispose();
        _networkIdObjects.Remove(handle);
        return Task.CompletedTask;
    }

    public Task<(ulong NetworkId, bool HasManager)> GetNetworkIdObjectInfoAsync(int handle, CancellationToken cancellationToken = default)
    {
        var item = NetworkIdObjectOf("networkIdObjectInfo", handle);
        return Task.FromResult((item.NetworkId, item.Manager is not null));
    }

    public Task<(int? Handle, int? Object)> LookupNetworkIdAsync(ulong networkId, CancellationToken cancellationToken = default) =>
        Task.FromResult(((NetworkIds.GetBaseObject(networkId) as HandleObject)?.Handle, NetworkIds.GetObject<HandleObject>(networkId)?.Handle));

    public Task ClearNetworkIdsAsync(CancellationToken cancellationToken = default)
    {
        NetworkIds.Clear();
        return Task.CompletedTask;
    }

    public Task InitGridAsync(float maxCellWidth, float maxCellHeight, GridRectangle world, CancellationToken cancellationToken = default)
    {
        if (!(maxCellWidth > 0f) || !(maxCellHeight > 0f))
            throw new PeerCommandException("gridInit", "the cell size must be positive");
        _grid ??= new GridSectorizer<int>();
        _grid.Init(maxCellWidth, maxCellHeight, world.MinX, world.MinY, world.MaxX, world.MaxY);
        return Task.CompletedTask;
    }

    public Task AddGridEntryAsync(int entry, GridRectangle bounds, CancellationToken cancellationToken = default)
    {
        if (!(bounds.MinX < bounds.MaxX && bounds.MinY < bounds.MaxY))
            throw new PeerCommandException("gridAdd", "the rectangle is empty");
        Grid("gridAdd").AddEntry(entry, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetGridEntriesAsync(GridRectangle bounds, CancellationToken cancellationToken = default)
    {
        var entries = new List<int>();
        Grid("gridGet").GetEntries(entries, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        return Task.FromResult<IReadOnlyList<int>>(entries);
    }

    public Task ClearGridAsync(CancellationToken cancellationToken = default)
    {
        Grid("gridClear").Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeltaGroupResult>> SerializeDeltaAsync(IReadOnlyList<DeltaGroup> groups, bool preSerializeTick = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var serializer = DeltaSerializer;
        if (preSerializeTick)
            serializer.OnPreSerializeTick();
        var context = new VariableDeltaSerializationContext();
        var results = new List<DeltaGroupResult>();
        foreach (var group in groups)
        {
            using var stream = new BitStream();
            var receipt = 0u;
            switch (group.Mode)
            {
                case "UNRELIABLE_ACKED":
                    receipt = group.SendTo is null ? group.Receipt : Started("deltaSerialize").ReserveReceipt().Value;
                    serializer.BeginUnreliableAckedSerialize(context, DeltaTarget(group), stream, receipt);
                    break;
                case "UNIQUE":
                    serializer.BeginUniqueSerialize(context, DeltaTarget(group), stream);
                    break;
                case "IDENTICAL":
                    serializer.BeginIdenticalSerialize(context, group.First, stream);
                    break;
                default:
                    throw new PeerCommandException("deltaSerialize", $"unknown mode '{group.Mode}'");
            }
            foreach (var value in group.Values)
                serializer.SerializeVariable(context, value);
            serializer.EndSerialize(context);

            var sent = false;
            if (group.SendTo is { } sendTo && stream.BitsWritten > 0)
            {
                using var message = new BitStream();
                message.WriteByte(group.MessageId);
                var bits = stream.CreateReader();
                message.Write(ref bits);
                var reliability = group.Reliability ?? (group.Mode == "UNRELIABLE_ACKED" ? Reliability.UnreliableWithAckReceipt : Reliability.ReliableOrdered);
                Started("deltaSerialize").Send(message, new RakNetGuid(sendTo), reliability, Priority.High, 0, new SendReceipt(receipt));
                sent = true;
            }
            results.Add(new DeltaGroupResult(stream.Written.ToArray(), stream.BitsWritten, receipt, sent));
        }
        return Task.FromResult<IReadOnlyList<DeltaGroupResult>>(results);
    }

    public Task DeltaPreSerializeTickAsync(CancellationToken cancellationToken = default)
    {
        DeltaSerializer.OnPreSerializeTick();
        return Task.CompletedTask;
    }

    public Task DeltaReceiptAsync(ulong target, uint receipt, bool arrived, CancellationToken cancellationToken = default)
    {
        DeltaSerializer.OnMessageReceipt(new RakNetGuid(target), receipt, arrived);
        return Task.CompletedTask;
    }

    public Task RemoveDeltaSystemAsync(ulong target, CancellationToken cancellationToken = default)
    {
        DeltaSerializer.RemoveRemoteSystemVariableHistory(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int?>> DeserializeDeltaAsync(ReadOnlyMemory<byte> data, int count, CancellationToken cancellationToken = default)
    {
        var reader = new BitStreamReader(data.Span);
        var values = new List<int?>();
        for (var i = 0; i < count; i++)
        {
            var value = 0;
            values.Add(VariableDeltaSerializer.DeserializeVariable(ref reader, ref value) ? value : null);
        }
        return Task.FromResult<IReadOnlyList<int?>>(values);
    }

    private NetworkIdManager NetworkIds => _networkIdManager ??= new NetworkIdManager();

    private VariableDeltaSerializer DeltaSerializer => _deltaSerializer ??= new VariableDeltaSerializer();

    private static RakNetGuid DeltaTarget(DeltaGroup group) =>
        group.Target is { } target ? new RakNetGuid(target) : throw new PeerCommandException("deltaSerialize", "missing field 'target'");

    private HandleObject NetworkIdObjectOf(string command, int handle) =>
        _networkIdObjects.TryGetValue(handle, out var item) ? item : throw new PeerCommandException(command, $"no object with handle {handle}");

    private GridSectorizer<int> Grid(string command) =>
        _grid ?? throw new PeerCommandException(command, "no GridSectorizer is initialised; send gridInit first");

    // interop-peer's Object: a NetworkIDObject the driver names by handle.
    private sealed class HandleObject(int handle) : NetworkIdObject
    {
        public int Handle { get; } = handle;
    }
}
