using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins.Tests.NetworkId;

public sealed class NetworkIdManagerTests
{
    private const ulong Start = 1_000_000;

    private readonly NetworkIdManager _manager = new(new FixedRandom(Start));

    [Fact]
    public void An_object_given_a_manager_gets_the_next_id_after_the_random_start()
    {
        // NetworkIDManager.cpp:24-27, 48-58: the start is Get64BitUniqueRandomNumber and each new ID is one more.
        using var first = new NetworkIdObject { Manager = _manager };
        using var second = new NetworkIdObject { Manager = _manager };

        Assert.Equal(Start + 1, first.NetworkId);
        Assert.Equal(Start + 2, second.NetworkId);
        Assert.Same(first, _manager.GetBaseObject(Start + 1));
        Assert.Same(second, _manager.GetBaseObject(Start + 2));
    }

    [Fact]
    public void A_new_object_has_no_id()
    {
        using var detached = new NetworkIdObject();

        Assert.Equal(RakNetConstants.UnassignedNetworkId, detached.NetworkId);
        Assert.Null(detached.Manager);
        Assert.Null(detached.Parent);
    }

    [Fact]
    public void An_id_in_use_is_skipped()
    {
        using var taken = new NetworkIdObject { NetworkId = Start + 1, Manager = _manager };

        using var fresh = new NetworkIdObject { Manager = _manager };

        Assert.Equal(Start + 1, taken.NetworkId);
        Assert.Equal(Start + 2, fresh.NetworkId);
    }

    [Fact]
    public void The_unassigned_id_is_never_given_out()
    {
        // NetworkIDManager.cpp:52-56: the counter steps over UNASSIGNED_NETWORK_ID and wraps to zero.
        var manager = new NetworkIdManager(new FixedRandom(ulong.MaxValue - 1));

        using var wrapped = new NetworkIdObject { Manager = manager };

        Assert.Equal(0ul, wrapped.NetworkId);
    }

    [Fact]
    public void An_object_given_an_id_by_the_authority_is_found_by_it()
    {
        // The receiving side of NetworkIDObject.h's usage: SetNetworkID with the ID from the packet.
        using var replica = new NetworkIdObject();
        replica.NetworkId = 42;
        replica.Manager = _manager;

        Assert.Equal(42ul, replica.NetworkId);
        Assert.Same(replica, _manager.GetBaseObject(42));
    }

    [Fact]
    public void Changing_the_id_of_a_tracked_object_moves_it()
    {
        using var item = new NetworkIdObject { Manager = _manager };

        item.NetworkId = 7;

        Assert.Null(_manager.GetBaseObject(Start + 1));
        Assert.Same(item, _manager.GetBaseObject(7));
    }

    [Fact]
    public void Setting_the_unassigned_id_detaches_the_object_from_its_manager()
    {
        // NetworkIDObject.cpp:70-74.
        using var item = new NetworkIdObject { Manager = _manager };

        item.NetworkId = RakNetConstants.UnassignedNetworkId;

        Assert.Null(item.Manager);
        Assert.Equal(RakNetConstants.UnassignedNetworkId, item.NetworkId);
        Assert.Null(_manager.GetBaseObject(Start + 1));
    }

    [Fact]
    public void Removing_the_manager_unassigns_the_id()
    {
        using var item = new NetworkIdObject { Manager = _manager };

        item.Manager = null;

        Assert.Equal(RakNetConstants.UnassignedNetworkId, item.NetworkId);
        Assert.Null(_manager.GetBaseObject(Start + 1));
    }

    [Fact]
    public void Moving_an_object_to_another_manager_keeps_its_id()
    {
        var other = new NetworkIdManager(new FixedRandom(5));
        using var item = new NetworkIdObject { Manager = _manager };

        item.Manager = other;

        Assert.Equal(Start + 1, item.NetworkId);
        Assert.Null(_manager.GetBaseObject(Start + 1));
        Assert.Same(item, other.GetBaseObject(Start + 1));
    }

    [Fact]
    public void A_disposed_object_is_no_longer_found()
    {
        var item = new NetworkIdObject { Manager = _manager };

        item.Dispose();

        Assert.Null(_manager.GetBaseObject(Start + 1));
    }

    [Fact]
    public void Ids_that_share_a_hash_bucket_are_each_found()
    {
        // NetworkIDToHashIndex is the ID modulo 1024 (NetworkIDManager.cpp:59-63); a bucket is a chain.
        using var a = new NetworkIdObject { NetworkId = 3, Manager = _manager };
        using var b = new NetworkIdObject { NetworkId = 3 + NetworkIdManager.HashLength, Manager = _manager };
        using var c = new NetworkIdObject { NetworkId = 3 + (2 * NetworkIdManager.HashLength), Manager = _manager };

        b.Dispose();

        Assert.Same(a, _manager.GetBaseObject(3));
        Assert.Null(_manager.GetBaseObject(3 + NetworkIdManager.HashLength));
        Assert.Same(c, _manager.GetBaseObject(3 + (2 * NetworkIdManager.HashLength)));
    }

    [Fact]
    public void Of_two_objects_given_one_id_the_first_tracked_is_found_until_it_goes()
    {
        // RakNet asserts on a duplicate; a release build chains it behind the first (NetworkIDManager.cpp:76-105).
        var first = new NetworkIdObject { NetworkId = 9, Manager = _manager };
        using var second = new NetworkIdObject { NetworkId = 9, Manager = _manager };

        Assert.Same(first, _manager.GetBaseObject(9));
        first.Dispose();
        Assert.Same(second, _manager.GetBaseObject(9));
    }

    [Fact]
    public void GetObject_returns_the_parent_when_one_is_set()
    {
        // GET_OBJECT_FROM_ID (NetworkIDManager.h:43-52): a NetworkIDObject held as a member points at its owner.
        var owner = new Owner();
        using var member = new NetworkIdObject { Parent = owner, Manager = _manager };
        using var plain = new NetworkIdObject { Manager = _manager };

        Assert.Same(owner, _manager.GetObject<Owner>(member.NetworkId));
        Assert.Same(member, _manager.GetBaseObject(member.NetworkId));
        Assert.Same(plain, _manager.GetObject<NetworkIdObject>(plain.NetworkId));
        Assert.Null(_manager.GetObject<Owner>(plain.NetworkId));
        Assert.Null(_manager.GetObject<Owner>(12345));
    }

    [Fact]
    public void A_derived_object_is_returned_as_itself()
    {
        using var derived = new Derived { Manager = _manager };

        Assert.Same(derived, _manager.GetObject<Derived>(derived.NetworkId));
    }

    [Fact]
    public void Clear_forgets_every_object()
    {
        using var item = new NetworkIdObject { Manager = _manager };

        _manager.Clear();

        Assert.Null(_manager.GetBaseObject(Start + 1));
        // The object still holds its manager and ID, as in RakNet; disposing it finds nothing to untrack.
        Assert.Same(_manager, item.Manager);
    }

    private sealed class Owner;

    private sealed class Derived : NetworkIdObject;

    // Stands in for Get64BitUniqueRandomNumber, so the IDs are known.
    private sealed class FixedRandom(ulong value) : RandomSource
    {
        public override ulong NextUInt64() => value;
    }
}
