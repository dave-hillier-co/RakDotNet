using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::AuthenticatedEncryption (crypt/tunnel/AuthenticatedEncryption.hpp, .cpp), the "Calico" datagram
/// protection: each datagram is <c>ChaCha(message || HMAC-MD5(iv || message)[..8]) || obfuscated iv[..3]</c>,
/// with a 1024-datagram anti-replay window. One object per connection, as ReliabilityLayer's auth_enc;
/// encryption and decryption are separately single-threaded. <see cref="Encrypt"/> and <see cref="Decrypt"/>
/// allocate nothing.
/// </summary>
internal sealed class AuthenticatedEncryption
{
    public const int MacBytes = 8;
    public const int IvBytes = 3;
    public const int OverheadBytes = IvBytes + MacBytes;

    private const int IvBits = IvBytes * 8;
    private const uint IvMsb = 1u << IvBits;
    private const uint IvMask = IvMsb - 1;
    private const uint IvFuzz = 0xCA7DCA7D;
    private const int BitmapBits = 1024;
    private const int BitmapWords = BitmapBits / 64;

    private readonly Skein _keyHash = new();
    private readonly ulong[] _ivBitmap = new ulong[BitmapWords];
    private HmacMd5 _localMacKey;
    private HmacMd5 _remoteMacKey;
    private ChaChaKey _localCipherKey;
    private ChaChaKey _remoteCipherKey;
    private bool _acceptOutOfOrder;

    public bool IsInitiator { get; private set; }

    /// <summary>The IV of the last datagram sent; the next one is one more.</summary>
    public ulong LocalIv { get; private set; }

    /// <summary>The newest IV accepted from the remote host.</summary>
    public ulong RemoteIv { get; private set; }

    /// <summary>AllowOutOfOrder; RakNet never turns it off.</summary>
    public void AllowOutOfOrder(bool allowed = true) => _acceptOutOfOrder = allowed;

    /// <summary>
    /// AuthenticatedEncryption::SetKey: derives both directions' MAC keys, cipher keys and IVs from the key
    /// agreement's hash and the key name. KeyAgreementResponder and KeyAgreementInitiator call it.
    /// </summary>
    internal bool SetKey(int keyBytes, Skein key, bool isInitiator, string keyName)
    {
        _acceptOutOfOrder = true;
        IsInitiator = isInitiator;
        Array.Clear(_ivBitmap);

        _keyHash.SetKey(key);
        _keyHash.BeginKdf();
        _keyHash.CrunchString(keyName);
        _keyHash.End();

        var kdf = new Skein();
        Derive(kdf, isInitiator ? "dsound.dll" : "opengl32.dll");
        _localMacKey.SetKey(kdf);
        Derive(kdf, isInitiator ? "opengl32.dll" : "dsound.dll");
        _remoteMacKey.SetKey(kdf);

        Span<byte> cipherKey = stackalloc byte[keyBytes];
        Derive(kdf, isInitiator ? "Advapi32.dll" : "OpenProcessToken");
        kdf.Generate(cipherKey);
        _localCipherKey.Set(cipherKey);
        Derive(kdf, isInitiator ? "OpenProcessToken" : "Advapi32.dll");
        kdf.Generate(cipherKey);
        _remoteCipherKey.Set(cipherKey);
        CryptographicOperations.ZeroMemory(cipherKey);

        Span<byte> iv = stackalloc byte[sizeof(ulong)];
        Derive(kdf, isInitiator ? "RichEd20.Dll" : "KERNEL32.DLL");
        kdf.Generate(iv);
        LocalIv = BinaryPrimitives.ReadUInt64LittleEndian(iv);
        Derive(kdf, isInitiator ? "KERNEL32.DLL" : "RichEd20.Dll");
        kdf.Generate(iv);
        RemoteIv = BinaryPrimitives.ReadUInt64LittleEndian(iv);
        kdf.Clear();
        return true;
    }

    /// <summary>GenerateProof: a MAC under the shared key showing the local host holds it.</summary>
    public void GenerateProof(Span<byte> proof)
    {
        var mac = new Skein();
        mac.SetKey(_keyHash);
        mac.BeginMac();
        mac.CrunchString(IsInitiator ? "ddraw.dll" : "shfolder.dll");
        mac.End();
        mac.Generate(proof);
        mac.Clear();
    }

    /// <summary>ValidateProof: false for a proof longer than 64 bytes or one that does not match.</summary>
    public bool ValidateProof(ReadOnlySpan<byte> proof)
    {
        if (proof.Length > KeyAgreementCommon.MaxBytes)
            return false;
        var mac = new Skein();
        mac.SetKey(_keyHash);
        mac.BeginMac();
        mac.CrunchString(IsInitiator ? "shfolder.dll" : "ddraw.dll");
        mac.End();
        Span<byte> expected = stackalloc byte[proof.Length];
        mac.Generate(expected);
        mac.Clear();
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    /// <summary>
    /// AuthenticatedEncryption::Encrypt: encrypts the first <paramref name="messageBytes"/> of
    /// <paramref name="buffer"/> in place and appends the overhead. False when the buffer has no room for it.
    /// </summary>
    public bool Encrypt(Span<byte> buffer, int messageBytes, out int encryptedBytes)
    {
        encryptedBytes = messageBytes;
        var outBytes = messageBytes + OverheadBytes;
        if (outBytes > buffer.Length)
            return false;
        var overhead = buffer[messageBytes..];
        var iv = ++LocalIv;

        Span<byte> ivBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ivBytes, iv);
        var mac = new HmacMd5();
        mac.RekeyFrom(in _localMacKey);
        mac.BeginMac();
        mac.Crunch(ivBytes);
        mac.Crunch(buffer[..messageBytes]);
        mac.End();
        mac.Generate(overhead[..MacBytes]);
        mac.Clear();

        var cipher = new ChaChaOutput(in _localCipherKey, iv);
        cipher.Crypt(buffer[..(messageBytes + MacBytes)], buffer);

        var truncatedIv = IvMask & ((uint)iv ^ BinaryPrimitives.ReadUInt32LittleEndian(overhead) ^ IvFuzz);
        overhead[MacBytes] = (byte)truncatedIv;
        overhead[MacBytes + 1] = (byte)(truncatedIv >> 8);
        overhead[MacBytes + 2] = (byte)(truncatedIv >> 16);
        encryptedBytes = outBytes;
        return true;
    }

    /// <summary>
    /// AuthenticatedEncryption::Decrypt: decrypts in place and gives the plaintext length. False, with the
    /// replay window unchanged, for a datagram that is too short, replayed, too old or fails the MAC.
    /// </summary>
    public bool Decrypt(Span<byte> buffer, out int messageBytes)
    {
        messageBytes = 0;
        if (buffer.Length < OverheadBytes)
            return false;
        var length = buffer.Length - OverheadBytes;
        var overhead = buffer[length..];

        var truncatedIv = ((uint)overhead[MacBytes + 2] << 16) | ((uint)overhead[MacBytes + 1] << 8) | overhead[MacBytes];
        truncatedIv = IvMask & (truncatedIv ^ BinaryPrimitives.ReadUInt32LittleEndian(overhead) ^ IvFuzz);
        var iv = ReconstructCounter(RemoteIv, truncatedIv);
        if (!IsValidIv(iv))
            return false;

        var cipher = new ChaChaOutput(in _remoteCipherKey, iv);
        cipher.Crypt(buffer[..(buffer.Length - IvBytes)], buffer);

        Span<byte> ivBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ivBytes, iv);
        var mac = new HmacMd5();
        mac.RekeyFrom(in _remoteMacKey);
        mac.BeginMac();
        mac.Crunch(ivBytes);
        mac.Crunch(buffer[..length]);
        mac.End();
        Span<byte> expected = stackalloc byte[MacBytes];
        mac.Generate(expected);
        mac.Clear();
        if (!CryptographicOperations.FixedTimeEquals(expected, overhead[..MacBytes]))
            return false;

        AcceptIv(iv);
        messageBytes = length;
        return true;
    }

    /// <summary>
    /// BitMath.hpp ReconstructCounter&lt;24&gt;: the full IV nearest <paramref name="lastAccepted"/> whose low 24
    /// bits are <paramref name="partialLowBits"/>.
    /// </summary>
    public static ulong ReconstructCounter(ulong lastAccepted, uint partialLowBits)
    {
        var diff = (int)(partialLowBits - (uint)(lastAccepted & IvMask));
        return ((lastAccepted & ~(ulong)IvMask) | partialLowBits)
            - (((IvMsb >> 1) - ((uint)diff & IvMask)) & IvMsb)
            + ((uint)diff & IvMsb);
    }

    /// <summary>The derived key material, little-endian, for checking key derivation against cat.</summary>
    internal KeyState ExportKeyState() => new(
        CipherWords(in _localCipherKey),
        CipherWords(in _remoteCipherKey),
        _localMacKey.CachedInitialStateBytes(),
        _localMacKey.CachedFinalStateBytes(),
        _remoteMacKey.CachedInitialStateBytes(),
        _remoteMacKey.CachedFinalStateBytes());

    /// <summary>Zeroes the keys, as the objects holding them do on destruction.</summary>
    public void Clear()
    {
        _keyHash.Clear();
        _localMacKey.Clear();
        _remoteMacKey.Clear();
        _localCipherKey.Clear();
        _remoteCipherKey.Clear();
        Array.Clear(_ivBitmap);
    }

    private static byte[] CipherWords(in ChaChaKey key)
    {
        ReadOnlySpan<uint> words = key.Words;
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), words[i]);
        return bytes;
    }

    private void Derive(Skein kdf, string label)
    {
        kdf.SetKey(_keyHash);
        kdf.BeginKdf();
        kdf.CrunchString(label);
        kdf.End();
    }

    private bool IsValidIv(ulong iv)
    {
        // AuthenticatedEncryption.cpp:174 truncates the distance to 32 bits.
        var delta = unchecked((int)(RemoteIv - iv));
        if (delta >= 0)
        {
            if (!_acceptOutOfOrder) return false;
            if (delta >= BitmapBits) return false;
            if ((_ivBitmap[delta >> 6] & (1UL << (delta & 63))) != 0) return false;
        }
        return true;
    }

    private void AcceptIv(ulong iv)
    {
        var delta = unchecked((int)(iv - RemoteIv));
        if (delta > 0)
        {
            if (delta >= BitmapBits)
            {
                _ivBitmap[0] = 1;
                Array.Clear(_ivBitmap, 1, BitmapWords - 1);
            }
            else
            {
                var wordShift = delta >> 6;
                var bitShift = delta & 63;
                var last = _ivBitmap[BitmapWords - 1 - wordShift];
                for (var i = BitmapWords - 1; i >= wordShift + 1; --i)
                {
                    var x = _ivBitmap[i - wordShift - 1];
                    // AuthenticatedEncryption.cpp:220 shifts x right by 64 when bit_shift is 0, which C++ leaves
                    // undefined and C# would treat as a shift by 0. The native build the vectors come from gives 0,
                    // the intended whole-word shift, so the replay window here does too.
                    _ivBitmap[i] = (last << bitShift) | (bitShift == 0 ? 0 : x >> (64 - bitShift));
                    last = x;
                }
                _ivBitmap[wordShift] = last << bitShift;
                for (var i = 0; i < wordShift; ++i)
                    _ivBitmap[i] = 0;
                _ivBitmap[0] |= 1;
            }
            RemoteIv = iv;
        }
        else
        {
            delta = -delta;
            _ivBitmap[delta >> 6] |= 1UL << (delta & 63);
        }
    }
}

/// <summary>Key material <see cref="AuthenticatedEncryption"/> derived: ChaCha key words and HMAC-MD5 states.</summary>
internal sealed record KeyState(
    byte[] LocalCipherWords,
    byte[] RemoteCipherWords,
    byte[] LocalMacInitial,
    byte[] LocalMacFinal,
    byte[] RemoteMacInitial,
    byte[] RemoteMacFinal);
