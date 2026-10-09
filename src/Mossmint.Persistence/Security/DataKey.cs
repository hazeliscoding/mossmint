using System.Security.Cryptography;

namespace Mossmint.Persistence.Security;

// A profile's 256-bit data key (ADR-0001). The database is keyed with it
// directly; it is only ever stored wrapped. Disposing zeroes the bytes, which
// limits how long the key sits in memory but can't scrub copies the runtime made.
public sealed class DataKey : IDisposable
{
    public const int Size = 32;

    private readonly byte[] _bytes;

    private DataKey(byte[] bytes) => _bytes = bytes;

    public bool IsDisposed { get; private set; }

    public static DataKey Generate() => new(RandomNumberGenerator.GetBytes(Size));

    public static DataKey FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == Size ? new DataKey(bytes.ToArray()) : throw new CryptographicException("A data key is 32 bytes.");

    public ReadOnlySpan<byte> Bytes => IsDisposed ? throw new ObjectDisposedException(nameof(DataKey)) : _bytes;

    // SQLCipher raw-key syntax: no key derivation runs, because the key is already random.
    internal string ToSqlCipherRawKey() => $"x'{Convert.ToHexString(Bytes)}'";

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_bytes);
        IsDisposed = true;
    }
}
