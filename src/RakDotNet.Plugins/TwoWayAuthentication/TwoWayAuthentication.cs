using System.Security.Cryptography;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// TwoWayAuthentication: two systems that share a password, named by a public identifier, prove it to each other without
/// sending it. Each challenge ends in a packet on both systems: ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS,
/// _FAILURE or _TIMEOUT on the challenger, and ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_SUCCESS or _FAILURE on the
/// challenged system.
/// </summary>
/// <remarks>
/// <para>
/// Ported from TwoWayAuthentication.cpp as RakNet builds it without LIBCAT_SECURITY: a 20-byte nonce and SHA-1 of the
/// nonce then the password. The challenger sends ID_TWO_WAY_AUTHENTICATION_NEGOTIATION with ID_NONCE_REQUEST (0); the
/// other system replies with ID_NONCE_REPLY (1), a big-endian u16 request ID and the nonce; the challenger answers with
/// ID_HASHED_NONCE_AND_PASSWORD (2), the request ID, the identifier as a RakString and the hash; the other system sends
/// back ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS or _FAILURE with the nonce, the hash and the identifier.
/// Every message is HIGH_PRIORITY and RELIABLE_ORDERED on channel 0. Identifiers and passwords are UTF-8, up to their
/// first NUL as a RakString holds them.
/// </para>
/// <para>
/// Results are injected at the head of the stream as generated locally: the ID, then the identifier as a RakString, from
/// the other system's GUID and address as far as they are known; a challenge made by GUID has no address. Results that
/// arrive from another system are dropped. An incoming failure is the hashed-nonce message itself with its first byte
/// rewritten (TwoWayAuthentication.cpp:375).
/// </para>
/// <para>
/// RakNet's timing is kept: timeouts are checked at most every 3 s, and the next check is 6 s after a check, so a
/// challenge times out 3 to 9 s after it is made (TwoWayAuthentication.cpp:159-175); each update forgets at most one
/// nonce older than 5 s (TwoWayAuthentication.cpp:105-112). Shutting down clears the passwords too. A truncated reply,
/// hash or result is ignored, where RakNet would read uninitialised bytes.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class TwoWayAuthentication : IRakPlugin
{
    /// <summary>TWO_WAY_AUTHENTICATION_NONCE_LENGTH without LIBCAT_SECURITY.</summary>
    public const int NonceLength = 20;

    /// <summary>HASHED_NONCE_AND_PW_LENGTH without LIBCAT_SECURITY: SHA1_LENGTH.</summary>
    public const int HashLength = 20;

    // NegotiationIdentifiers (TwoWayAuthentication.cpp:28-33).
    private const byte NonceRequest = 0;
    private const byte NonceReply = 1;
    private const byte HashedNonceAndPassword = 2;

    // CHALLENGE_MINIMUM_TIMEOUT (TwoWayAuthentication.h), and NonceGenerator::Update's literal 5000.
    private const ulong ChallengeMinimumTimeoutMs = 3000;
    private const ulong NonceTimeoutMs = 5000;

    private readonly Lock _gate = new();

    // DataStructures::Hash<RakString, RakString>: keyed by the identifier's bytes, held as one char per byte.
    private readonly Dictionary<string, byte[]> _passwords = new(StringComparer.Ordinal);
    private readonly List<PendingChallenge> _outgoingChallenges = [];
    private readonly List<GeneratedNonce> _generatedNonces = [];
    private ushort _nextRequestId;
    private ulong _whenLastTimeoutCheck;
    private IPluginHost? _host;

    /// <summary>
    /// AddPassword: accepts <paramref name="password"/> for challenges naming <paramref name="identifier"/>. False when
    /// either is empty, they are equal, or the identifier is already in use.
    /// </summary>
    public bool AddPassword(string identifier, string password)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(password);
        var identifierBytes = ToRakString(identifier);
        var passwordBytes = ToRakString(password);
        if (passwordBytes.Length == 0 || identifierBytes.Length == 0 || passwordBytes.AsSpan().SequenceEqual(identifierBytes))
            return false;
        lock (_gate)
            return _passwords.TryAdd(Key(identifierBytes), passwordBytes);
    }

    /// <summary>
    /// Challenge: asks <paramref name="remoteSystem"/> to prove it holds the password of <paramref name="identifier"/>.
    /// False when the identifier was never added; nothing checks that the system is connected.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a peer.</exception>
    public bool Challenge(string identifier, AddressOrGuid remoteSystem)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var identifierBytes = ToRakString(identifier);
        lock (_gate)
        {
            if (!_passwords.ContainsKey(Key(identifierBytes)))
                return false;
            var host = Host;
            ReadOnlySpan<byte> request = [(byte)MessageId.TwoWayAuthenticationNegotiation, NonceRequest];
            host.Send(request, remoteSystem, Reliability.ReliableOrdered, Priority.High, 0);
            _outgoingChallenges.Add(new PendingChallenge(identifierBytes, new RemoteSystem(remoteSystem.RakNetGuid, remoteSystem.Address), host.GetTime()));
            return true;
        }
    }

    /// <summary>Clear: forgets every password, pending challenge and nonce.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _outgoingChallenges.Clear();
            _passwords.Clear();
            _generatedNonces.Clear();
        }
    }

    private IPluginHost Host => _host ?? throw new InvalidOperationException("The TwoWayAuthentication is not attached to a peer.");

    void IRakPlugin.OnAttached(IPluginHost host)
    {
        _host = host;
        // The constructor's whenLastTimeoutCheck=RakNet::GetTime(), on the host's clock.
        lock (_gate)
            _whenLastTimeoutCheck = host.GetTime();
    }

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnRakPeerShutdown: Clear.</summary>
    void IRakPlugin.OnShutdown() => Clear();

    /// <summary>Update (TwoWayAuthentication.cpp:159-175).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host is not { } host)
            return;
        lock (_gate)
        {
            var now = host.GetTime();
            if (_generatedNonces.Count > 0 && GreaterThan(now - NonceTimeoutMs, _generatedNonces[0].WhenGenerated))
                _generatedNonces.RemoveAt(0);
            if (!GreaterThan(now - ChallengeMinimumTimeoutMs, _whenLastTimeoutCheck))
                return;
            while (_outgoingChallenges.Count > 0 && GreaterThan(now - ChallengeMinimumTimeoutMs, _outgoingChallenges[0].Time))
            {
                var challenge = _outgoingChallenges[0];
                _outgoingChallenges.RemoveAt(0);
                PushToUser(host, MessageId.TwoWayAuthenticationOutgoingChallengeTimeout, challenge.Identifier, challenge.RemoteSystem);
            }
            _whenLastTimeoutCheck = now + ChallengeMinimumTimeoutMs;
        }
    }

    /// <summary>OnClosedConnection (TwoWayAuthentication.cpp:232-255).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        var guid = connection.RakNetGuid;
        var address = connection.Address;
        lock (_gate)
        {
            _outgoingChallenges.RemoveAll(c =>
                (guid.IsAssigned && c.RemoteSystem.RakNetGuid == guid) || (address.IsAssigned && c.RemoteSystem.Address == address));
            // NonceGenerator::ClearByAddress with the GUID, else the address, as an AddressOrGUID holding only that.
            var cleared = guid.IsAssigned ? new RemoteSystem(guid, SystemAddress.Unassigned) : new RemoteSystem(RakNetGuid.Unassigned, address);
            _generatedNonces.RemoveAll(n => n.RemoteSystem.Matches(cleared));
        }
    }

    /// <summary>OnReceive (TwoWayAuthentication.cpp:176-227).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host is not { } host)
            return PluginResult.Continue;
        switch ((MessageId)data[0])
        {
            case MessageId.TwoWayAuthenticationNegotiation:
                if (data.Length >= 2)
                {
                    var sender = new RemoteSystem(e.RakNetGuid, e.Address);
                    switch (data[1])
                    {
                        case NonceRequest:
                            OnNonceRequest(host, sender);
                            break;
                        case NonceReply:
                            OnNonceReply(host, e.GetReader(), sender);
                            break;
                        case HashedNonceAndPassword:
                            return OnHashedNonceAndPassword(host, e, sender);
                    }
                }
                return PluginResult.Consume;
            case MessageId.TwoWayAuthenticationOutgoingChallengeFailure:
            case MessageId.TwoWayAuthenticationOutgoingChallengeSuccess:
                if (e.IsLocal)
                    return PluginResult.Continue;
                OnPasswordResult(host, e.GetReader(), data[0], new RemoteSystem(e.RakNetGuid, e.Address));
                return PluginResult.Consume;
            case MessageId.TwoWayAuthenticationIncomingChallengeSuccess:
            case MessageId.TwoWayAuthenticationIncomingChallengeFailure:
            case MessageId.TwoWayAuthenticationOutgoingChallengeTimeout:
                // These should only be generated locally.
                return e.IsLocal ? PluginResult.Continue : PluginResult.Consume;
            default:
                return PluginResult.Continue;
        }
    }

    // OnNonceRequest and NonceGenerator::GetNonce (TwoWayAuthentication.cpp:42-56, 276-291).
    private void OnNonceRequest(IPluginHost host, RemoteSystem sender)
    {
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);
        lock (_gate)
        {
            var requestId = _nextRequestId++;
            _generatedNonces.Add(new GeneratedNonce(nonce, sender, requestId, host.GetTime()));
            Span<byte> reply = stackalloc byte[2 + sizeof(ushort) + NonceLength];
            var writer = new BitStreamWriter(reply);
            writer.WriteByte((byte)MessageId.TwoWayAuthenticationNegotiation);
            writer.WriteByte(NonceReply);
            writer.WriteUInt16(requestId);
            writer.WriteAlignedBytes(nonce);
            host.Send(writer, sender.Target, Reliability.ReliableOrdered, Priority.High, 0);
        }
    }

    // OnNonceReply (TwoWayAuthentication.cpp:292-334): the first challenge to the sender that has not sent its hash does.
    private void OnNonceReply(IPluginHost host, BitStreamReader reader, RemoteSystem sender)
    {
        reader.IgnoreBytes(2);
        if (!reader.TryReadUInt16(out var requestId) || !reader.TryReadAlignedBytes(NonceLength, out var theirNonce))
            return;
        lock (_gate)
        {
            foreach (var challenge in _outgoingChallenges)
            {
                if (!challenge.RemoteSystem.Matches(sender) || challenge.SentHash)
                    continue;
                challenge.SentHash = true;
                if (_passwords.TryGetValue(Key(challenge.Identifier), out var password))
                {
                    Span<byte> hash = stackalloc byte[HashLength];
                    Hash(theirNonce, password, hash);
                    var message = new byte[2 + sizeof(ushort) + sizeof(ushort) + challenge.Identifier.Length + HashLength];
                    var writer = new BitStreamWriter(message);
                    writer.WriteByte((byte)MessageId.TwoWayAuthenticationNegotiation);
                    writer.WriteByte(HashedNonceAndPassword);
                    writer.WriteUInt16(requestId);
                    writer.WriteRakString(challenge.Identifier.AsSpan());
                    writer.WriteAlignedBytes(hash);
                    host.Send(writer, sender.Target, Reliability.ReliableOrdered, Priority.High, 0);
                }
                return;
            }
        }
    }

    // OnHashedNonceAndPassword (TwoWayAuthentication.cpp:335-385).
    private PluginResult OnHashedNonceAndPassword(IPluginHost host, PeerEvent e, RemoteSystem sender)
    {
        var reader = e.GetReader();
        reader.IgnoreBytes(2);
        if (!reader.TryReadUInt16(out var requestId) || !reader.TryReadRakStringBytes(out var identifierSpan)
            || !reader.TryReadAlignedBytes(HashLength, out var remoteHash))
            return PluginResult.Consume;
        var identifier = identifierSpan.ToArray();
        byte[] usedNonce;
        bool passed;
        lock (_gate)
        {
            if (!TryTakeNonce(requestId, sender, out usedNonce))
                return PluginResult.Consume;
            passed = false;
            if (_passwords.TryGetValue(Key(identifier), out var password))
            {
                Span<byte> hash = stackalloc byte[HashLength];
                Hash(usedNonce, password, hash);
                passed = hash.SequenceEqual(remoteHash);
            }
        }

        var result = passed ? MessageId.TwoWayAuthenticationOutgoingChallengeSuccess : MessageId.TwoWayAuthenticationOutgoingChallengeFailure;
        var message = new byte[1 + NonceLength + HashLength + sizeof(ushort) + identifier.Length];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)result);
        writer.WriteAlignedBytes(usedNonce);
        writer.WriteAlignedBytes(remoteHash);
        writer.WriteRakString(identifier.AsSpan());
        host.Send(writer, sender.Target, Reliability.ReliableOrdered, Priority.High, 0);
        if (passed)
        {
            PushToUser(host, MessageId.TwoWayAuthenticationIncomingChallengeSuccess, identifier, sender);
            return PluginResult.Consume;
        }
        // Incoming failure: the arrived packet goes on to the user with its header changed.
        e.RewriteId(MessageId.TwoWayAuthenticationIncomingChallengeFailure);
        return PluginResult.Continue;
    }

    // OnPasswordResult (TwoWayAuthentication.cpp:386-422): only a result whose hash this system's password reproduces
    // ends the matching challenge.
    private void OnPasswordResult(IPluginHost host, BitStreamReader reader, byte resultId, RemoteSystem sender)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadAlignedBytes(NonceLength, out var usedNonce) || !reader.TryReadAlignedBytes(HashLength, out var hashedNonceAndPassword)
            || !reader.TryReadRakStringBytes(out var identifier))
            return;
        lock (_gate)
        {
            if (!_passwords.TryGetValue(Key(identifier), out var password))
                return;
            Span<byte> testHash = stackalloc byte[HashLength];
            Hash(usedNonce, password, testHash);
            if (!testHash.SequenceEqual(hashedNonceAndPassword))
                return;
            for (var i = 0; i < _outgoingChallenges.Count; i++)
            {
                var challenge = _outgoingChallenges[i];
                if (challenge.Identifier.AsSpan().SequenceEqual(identifier) && challenge.RemoteSystem.Matches(sender) && challenge.SentHash)
                {
                    _outgoingChallenges.RemoveAt(i);
                    PushToUser(host, (MessageId)resultId, identifier, sender);
                    return;
                }
            }
        }
    }

    // NonceGenerator::GetNonceById with popIfFound (TwoWayAuthentication.cpp:57-81): the first nonce with the request ID,
    // if it was handed to the sender.
    private bool TryTakeNonce(ushort requestId, RemoteSystem sender, out byte[] nonce)
    {
        for (var i = 0; i < _generatedNonces.Count; i++)
        {
            var generated = _generatedNonces[i];
            if (generated.RequestId != requestId)
                continue;
            if (!sender.Matches(generated.RemoteSystem))
                break;
            nonce = generated.Nonce;
            _generatedNonces.RemoveAt(i);
            return true;
        }
        nonce = [];
        return false;
    }

    // PushToUser (TwoWayAuthentication.cpp:262-275): pushed at the head, generated locally, from the system as known.
    private static void PushToUser(IPluginHost host, MessageId messageId, ReadOnlySpan<byte> identifier, RemoteSystem remoteSystem)
    {
        Span<byte> output = stackalloc byte[1 + sizeof(ushort) + identifier.Length];
        var writer = new BitStreamWriter(output);
        writer.WriteByte((byte)messageId);
        if (!identifier.IsEmpty)
            writer.WriteRakString(identifier);
        host.Inject(writer.Written, remoteSystem.Address, remoteSystem.RakNetGuid, atHead: true, generatedLocally: true);
    }

    // Hash without LIBCAT_SECURITY (TwoWayAuthentication.cpp:423-440): CSHA1 over the nonce, then the password.
#pragma warning disable CA5350 // SHA-1 is what the wire protocol specifies; it is not chosen here.
    private static void Hash(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> password, Span<byte> destination)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(nonce);
        sha1.AppendData(password);
        sha1.GetHashAndReset(destination);
    }
#pragma warning restore CA5350

    // RakString(const char*) of the UTF-8 text: up to the first NUL.
    private static byte[] ToRakString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var end = Array.IndexOf(bytes, (byte)0);
        return end < 0 ? bytes : bytes[..end];
    }

    private static string Key(ReadOnlySpan<byte> identifier) => Encoding.Latin1.GetString(identifier);

    // GreaterThan(RakNet::Time, RakNet::Time) (GetTime.h): a is after b, across wraparound.
    private static bool GreaterThan(ulong a, ulong b) => b != a && b - a > ulong.MaxValue / 2;

    /// <summary>
    /// RakNet's AddressOrGUID, which may hold both. Equality is AddressOrGUID::operator== (RakNetTypes.h:422): the GUIDs
    /// match and this one is assigned, or the addresses do and this one is assigned.
    /// </summary>
    private readonly record struct RemoteSystem(RakNetGuid RakNetGuid, SystemAddress Address)
    {
        public AddressOrGuid Target => RakNetGuid.IsAssigned ? RakNetGuid : Address;

        public bool Matches(RemoteSystem other) =>
            (RakNetGuid.IsAssigned && RakNetGuid == other.RakNetGuid) || (Address.IsAssigned && Address == other.Address);
    }

    private sealed class PendingChallenge(byte[] identifier, RemoteSystem remoteSystem, ulong time)
    {
        public byte[] Identifier { get; } = identifier;

        public RemoteSystem RemoteSystem { get; } = remoteSystem;

        public ulong Time { get; } = time;

        public bool SentHash { get; set; }
    }

    private sealed record GeneratedNonce(byte[] Nonce, RemoteSystem RemoteSystem, ushort RequestId, ulong WhenGenerated);
}
