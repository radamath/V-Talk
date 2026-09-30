using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>
/// Karşılıklı kimlik doğrulamalı, ileri gizlilikli (forward secrecy) şifreli TCP kanalı.
///
/// El sıkışma:
///   C → S : ClientHello  = [magic][nonceC][ephC]
///   S → C : ServerHello  = [nonceS][ephS][idS][sig_S(H("S" | CH | nonceS | ephS | idS))]
///   C → S : ClientAuth   = [idC][sig_C(H("C" | CH | SH | idC))]
/// Oturum anahtarları = HKDF(ECDH(ephC, ephS), salt = H(CH | SH | CA)), yön başına ayrı AES-256-GCM anahtarı.
/// PeerId = H(idPub) olduğundan istemci, keşifte gördüğü PeerId ile sunucunun anahtarını eşleştirir (MITM engellenir).
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public const int MaxPlaintext = 1024 * 1024;
    private const int MaxHandshakeFrame = 4096;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VTALK/1");
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly AesGcm _sendKey;
    private readonly AesGcm _recvKey;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private ulong _sendSeq;
    private ulong _recvSeq;
    private int _disposed;

    public string RemotePeerId { get; }
    public byte[] RemotePublicKey { get; }
    public IPAddress RemoteAddress { get; }

    private SecureChannel(TcpClient tcp, byte[] sendKey, byte[] recvKey, byte[] remotePub)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _sendKey = new AesGcm(sendKey, 16);
        _recvKey = new AesGcm(recvKey, 16);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(recvKey);
        RemotePublicKey = remotePub;
        RemotePeerId = CryptoUtil.PeerIdFromPublicKey(remotePub);
        RemoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
    }

    public static async Task<SecureChannel> ConnectAsync(IPEndPoint endpoint, Identity identity, string expectedPeerId, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HandshakeTimeout);
        try
        {
            await tcp.ConnectAsync(endpoint, cts.Token);
            ConfigureSocket(tcp);
            var stream = tcp.GetStream();

            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var nonceC = RandomNumberGenerator.GetBytes(32);
            var ch = Pack(Magic, nonceC, eph.PublicKey.ExportSubjectPublicKeyInfo());
            await WriteRawAsync(stream, ch, cts.Token);

            var sh = await ReadRawAsync(stream, cts.Token);
            var shParts = Unpack(sh, 4);
            byte[] nonceS = shParts[0], ephS = shParts[1], idS = shParts[2], sigS = shParts[3];
            if (nonceS.Length != 32) throw new ProtocolException("Geçersiz ServerHello.");

            var serverPeerId = CryptoUtil.PeerIdFromPublicKey(idS);
            if (!CryptoUtil.FixedEquals(serverPeerId, expectedPeerId))
                throw new ProtocolException("Karşı tarafın kimliği beklenen kimlikle eşleşmiyor.");
            var serverSigned = Hash("S", ch, Pack(nonceS, ephS, idS));
            if (!CryptoUtil.Verify(idS, serverSigned, sigS))
                throw new ProtocolException("Sunucu imzası geçersiz.");

            var idC = identity.PublicKey;
            var sigC = identity.Sign(Hash("C", ch, sh, idC));
            var ca = Pack(idC, sigC);
            await WriteRawAsync(stream, ca, cts.Token);

            var (c2s, s2c) = DeriveKeys(eph, ephS, Hash("T", ch, sh, ca));
            return new SecureChannel(tcp, sendKey: c2s, recvKey: s2c, remotePub: idS);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static async Task<SecureChannel> AcceptAsync(TcpClient tcp, Identity identity, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HandshakeTimeout);
        ConfigureSocket(tcp);
        var stream = tcp.GetStream();

        var ch = await ReadRawAsync(stream, cts.Token);
        var chParts = Unpack(ch, 3);
        if (!chParts[0].AsSpan().SequenceEqual(Magic) || chParts[1].Length != 32)
            throw new ProtocolException("Geçersiz ClientHello.");
        var ephC = chParts[2];

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonceS = RandomNumberGenerator.GetBytes(32);
        var ephS = eph.PublicKey.ExportSubjectPublicKeyInfo();
        var idS = identity.PublicKey;
        var sigS = identity.Sign(Hash("S", ch, Pack(nonceS, ephS, idS)));
        var sh = Pack(nonceS, ephS, idS, sigS);
        await WriteRawAsync(stream, sh, cts.Token);

        var ca = await ReadRawAsync(stream, cts.Token);
        var caParts = Unpack(ca, 2);
        byte[] idC = caParts[0], sigC = caParts[1];
        if (!CryptoUtil.Verify(idC, Hash("C", ch, sh, idC), sigC))
            throw new ProtocolException("İstemci imzası geçersiz.");

        var (c2s, s2c) = DeriveKeys(eph, ephC, Hash("T", ch, sh, ca));
        return new SecureChannel(tcp, sendKey: s2c, recvKey: c2s, remotePub: idC);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        if (plaintext.Length > MaxPlaintext) throw new ProtocolException("Çerçeve çok büyük.");
        var frame = new byte[4 + plaintext.Length + 16];
        await _writeLock.WaitAsync(ct);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(frame, plaintext.Length + 16);
            var nonce = MakeNonce(_sendSeq++);
            _sendKey.Encrypt(nonce, plaintext.Span, frame.AsSpan(4, plaintext.Length),
                frame.AsSpan(4 + plaintext.Length, 16), frame.AsSpan(0, 4));
            await _stream.WriteAsync(frame, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header, ct);
            int len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len < 16 || len > MaxPlaintext + 16) throw new ProtocolException("Geçersiz çerçeve uzunluğu.");
            var body = new byte[len];
            await _stream.ReadExactlyAsync(body, ct);
            var plain = new byte[len - 16];
            var nonce = MakeNonce(_recvSeq++);
            _recvKey.Decrypt(nonce, body.AsSpan(0, len - 16), body.AsSpan(len - 16, 16), plain, header);
            return plain;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private static byte[] MakeNonce(ulong seq)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), seq);
        return nonce;
    }

    private static (byte[] c2s, byte[] s2c) DeriveKeys(ECDiffieHellman own, byte[] otherSpki, byte[] transcript)
    {
        using var other = ECDiffieHellman.Create();
        try
        {
            other.ImportSubjectPublicKeyInfo(otherSpki, out int read);
            if (read != otherSpki.Length || other.KeySize != 256) throw new ProtocolException("Geçersiz geçici anahtar.");
        }
        catch (CryptographicException)
        {
            throw new ProtocolException("Geçersiz geçici anahtar.");
        }
        var shared = own.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        try
        {
            var c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, Encoding.ASCII.GetBytes("vtalk c2s"));
            var s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, Encoding.ASCII.GetBytes("vtalk s2c"));
            return (c2s, s2c);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static byte[] Hash(string label, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.ASCII.GetBytes("VTALK-HS-" + label));
        Span<byte> len = stackalloc byte[4];
        foreach (var p in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(len, p.Length);
            h.AppendData(len);
            h.AppendData(p);
        }
        return h.GetHashAndReset();
    }

    private static byte[] Pack(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[2];
        foreach (var p in parts)
        {
            if (p.Length > ushort.MaxValue) throw new ProtocolException("Alan çok büyük.");
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)p.Length);
            ms.Write(len);
            ms.Write(p);
        }
        return ms.ToArray();
    }

    private static byte[][] Unpack(byte[] data, int expectedCount)
    {
        var result = new byte[expectedCount][];
        int offset = 0;
        for (int i = 0; i < expectedCount; i++)
        {
            if (offset + 2 > data.Length) throw new ProtocolException("Eksik el sıkışma verisi.");
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 2;
            if (offset + len > data.Length) throw new ProtocolException("Eksik el sıkışma verisi.");
            result[i] = data.AsSpan(offset, len).ToArray();
            offset += len;
        }
        if (offset != data.Length) throw new ProtocolException("Fazladan el sıkışma verisi.");
        return result;
    }

    private static async Task WriteRawAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        var frame = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length);
        data.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
    }

    private static async Task<byte[]> ReadRawAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > MaxHandshakeFrame) throw new ProtocolException("Geçersiz el sıkışma çerçevesi.");
        var data = new byte[len];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static void ConfigureSocket(TcpClient tcp)
    {
        tcp.NoDelay = true;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        try { _tcp.Dispose(); } catch { }
        _sendKey.Dispose();
        _recvKey.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProtocolException(string message) : Exception(message);
