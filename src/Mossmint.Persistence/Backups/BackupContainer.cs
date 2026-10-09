using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Mossmint.Persistence.Security;

namespace Mossmint.Persistence.Backups;

// The plaintext part of a .mmbak file. It names no person and holds no data:
// just what's needed to unwrap the key and decrypt the payload. The whole
// header is authenticated by every payload chunk.
internal sealed record BackupHeader(
    string Format,
    int FormatVersion,
    IReadOnlyList<KeyEnvelope> Envelopes,
    string PayloadSalt,
    string BaseNonce,
    int ChunkSize);

public sealed class BackupFormatException(string message, bool unsupportedVersion = false) : IOException(message)
{
    public bool UnsupportedVersion { get; } = unsupportedVersion;
}

// .mmbak layout (docs/operations/backup-and-restore.md):
//   "MMBACKUP" | u16 container version | u32 header length | header JSON |
//   chunks: u32 plaintext length | u8 final flag | ciphertext | 16-byte GCM tag
// The payload key is HKDF(data key, payload salt). Each chunk's nonce is the base
// nonce with the chunk index mixed into its last four bytes, and its associated
// data is SHA-256(header) | index | final flag, so chunks can't be dropped,
// reordered, truncated or moved between backups without failing authentication.
internal static class BackupContainer
{
    public const string FormatName = "mossmint-backup";
    public const int FormatVersion = 1;
    private const ushort ContainerVersion = 1;
    private const int TagSize = 16;
    private const int DefaultChunkSize = 1 << 20;
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly byte[] Magic = "MMBACKUP"u8.ToArray();

    public static void Write(Stream output, IReadOnlyList<KeyEnvelope> envelopes, DataKey key, Stream payload, int chunkSize = DefaultChunkSize)
    {
        var header = new BackupHeader(
            FormatName, FormatVersion, envelopes,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
            chunkSize);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);

        output.Write(Magic);
        Span<byte> fixedFields = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedFields, ContainerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedFields[2..], (uint)headerBytes.Length);
        output.Write(fixedFields);
        output.Write(headerBytes);

        using var aes = new AesGcm(PayloadKey(key, header), TagSize);
        var headerHash = SHA256.HashData(headerBytes);
        var baseNonce = Convert.FromBase64String(header.BaseNonce);
        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        var tag = new byte[TagSize];
        var chunkHeader = new byte[5];

        var read = ReadFull(payload, plain);
        for (uint index = 0; ; index++)
        {
            var next = new byte[chunkSize];
            var nextRead = read == chunkSize ? ReadFull(payload, next) : 0;
            var final = nextRead == 0;

            aes.Encrypt(Nonce(baseNonce, index), plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, Aad(headerHash, index, final));
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader, (uint)read);
            chunkHeader[4] = final ? (byte)1 : (byte)0;
            output.Write(chunkHeader);
            output.Write(cipher.AsSpan(0, read));
            output.Write(tag);

            if (final)
            {
                return;
            }

            (plain, read) = (next, nextRead);
        }
    }

    public static BackupHeader ReadHeader(Stream input, out byte[] headerBytes)
    {
        var magic = new byte[Magic.Length];
        if (ReadFull(input, magic) != magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new BackupFormatException("This file isn't a Mossmint backup.");
        }

        var fixedFields = new byte[6];
        if (ReadFull(input, fixedFields) != fixedFields.Length)
        {
            throw new BackupFormatException("The backup ends too early.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(fixedFields);
        if (version > ContainerVersion)
        {
            throw new BackupFormatException($"This backup was made by a newer Mossmint (container {version}).", unsupportedVersion: true);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields.AsSpan(2));
        if (length is 0 or > MaxHeaderBytes)
        {
            throw new BackupFormatException("The backup header is damaged.");
        }

        headerBytes = new byte[length];
        if (ReadFull(input, headerBytes) != headerBytes.Length)
        {
            throw new BackupFormatException("The backup ends too early.");
        }

        BackupHeader header;
        try
        {
            header = JsonSerializer.Deserialize<BackupHeader>(headerBytes) ?? throw new BackupFormatException("The backup header is empty.");
        }
        catch (JsonException)
        {
            throw new BackupFormatException("The backup header is damaged.");
        }

        if (header.Format != FormatName)
        {
            throw new BackupFormatException("This file isn't a Mossmint backup.");
        }

        if (header.FormatVersion > FormatVersion)
        {
            throw new BackupFormatException($"This backup was made by a newer Mossmint (format {header.FormatVersion}).", unsupportedVersion: true);
        }

        if (header.ChunkSize is < 1024 or > 64 * 1024 * 1024)
        {
            throw new BackupFormatException("The backup header is damaged.");
        }

        return header;
    }

    // Decrypts the payload into output. Any tampering, truncation or trailing
    // data throws before the caller can treat the result as complete.
    public static void ReadPayload(Stream input, BackupHeader header, byte[] headerBytes, DataKey key, Stream output)
    {
        using var aes = new AesGcm(PayloadKey(key, header), TagSize);
        var headerHash = SHA256.HashData(headerBytes);
        var baseNonce = Convert.FromBase64String(header.BaseNonce);
        var cipher = new byte[header.ChunkSize];
        var plain = new byte[header.ChunkSize];
        var tag = new byte[TagSize];
        var chunkHeader = new byte[5];

        for (uint index = 0; ; index++)
        {
            if (ReadFull(input, chunkHeader) != chunkHeader.Length)
            {
                throw new BackupFormatException("The backup ends too early; it may be incomplete.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var final = chunkHeader[4] == 1;
            if (length > header.ChunkSize || chunkHeader[4] > 1)
            {
                throw new BackupFormatException("The backup is damaged.");
            }

            if (ReadFull(input, cipher.AsSpan(0, length)) != length || ReadFull(input, tag) != TagSize)
            {
                throw new BackupFormatException("The backup ends too early; it may be incomplete.");
            }

            try
            {
                aes.Decrypt(Nonce(baseNonce, index), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), Aad(headerHash, index, final));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new BackupFormatException("The backup is damaged or has been changed.");
            }

            output.Write(plain.AsSpan(0, length));
            if (final)
            {
                if (input.ReadByte() != -1)
                {
                    throw new BackupFormatException("The backup has unexpected data after its end.");
                }

                return;
            }
        }
    }

    private static byte[] PayloadKey(DataKey key, BackupHeader header)
    {
        var payloadKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Bytes, payloadKey, Convert.FromBase64String(header.PayloadSalt), "mossmint/backup/v1/payload"u8);
        return payloadKey;
    }

    private static byte[] Nonce(byte[] baseNonce, uint index)
    {
        var nonce = (byte[])baseNonce.Clone();
        var counter = BinaryPrimitives.ReadUInt32BigEndian(nonce.AsSpan(8)) ^ index;
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] Aad(byte[] headerHash, uint index, bool final)
    {
        var aad = new byte[headerHash.Length + 5];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(headerHash.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
