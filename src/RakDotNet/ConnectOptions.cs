using RakDotNet.Security;

namespace RakDotNet;

/// <summary>The arguments of RakPeer::Connect after the host and port.</summary>
public sealed record ConnectOptions
{
    /// <summary>At most 255 bytes; longer throws. Copied on assignment.</summary>
    public ReadOnlyMemory<byte> Password { get; init; }

    /// <summary>RakNet's publicKey argument: PublicKey and PublicKeyMode.</summary>
    public ConnectSecurity Security { get; init; } = ConnectSecurity.Insecure;

    /// <summary>connectionSocketIndex; also how ConnectWithSocket selects an adopted socket.</summary>
    public int SocketIndex { get; init; }

    /// <summary>sendConnectionAttemptCount, 12 (RakPeerInterface.h:139).</summary>
    public int AttemptCount { get; init; } = 12;

    /// <summary>timeBetweenSendConnectionAttemptsMS, 500 ms (RakPeerInterface.h:139).</summary>
    public TimeSpan AttemptInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>timeoutTime; <see cref="TimeSpan.Zero"/> means <see cref="RakPeer.DefaultTimeout"/>.</summary>
    public TimeSpan Timeout { get; init; }
}
