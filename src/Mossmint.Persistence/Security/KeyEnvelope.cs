using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Mossmint.Persistence.Security;

public enum EnvelopeKind
{
    Passphrase,
    RecoveryKey,
    ThisDevice,
}

// Argon2id cost, stored with every passphrase envelope so it can be raised later
// without breaking existing profiles.
public sealed record Argon2Settings(long MemoryKiB, long Passes, int Parallelism)
{
    // ADR-0001 starting point: 64 MiB, 3 passes, one lane (libsodium supports one).
    public static Argon2Settings Default { get; } = new(64 * 1024, 3, 1);
}

// The data key wrapped by one secret. Nothing in an envelope reveals the key or
// the secret: a passphrase envelope holds a salt, cost settings, a nonce and
// AES-256-GCM ciphertext; a device envelope holds a DPAPI blob.
public sealed record KeyEnvelope(
    EnvelopeKind Kind,
    int Version,
    string Salt,
    string Nonce,
    string Ciphertext,
    Argon2Settings? Argon2 = null);

public sealed class WrongSecretException() : CryptographicException("The passphrase or recovery key doesn't match this profile.");

public static class KeyEnvelopes
{
    private const int CurrentVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] DeviceEntropy = "mossmint/device/v1"u8.ToArray();

    public static KeyEnvelope WrapWithPassphrase(DataKey key, string passphrase, Argon2Settings? settings = null)
    {
        settings ??= Argon2Settings.Default;
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var kek = new Kek(DerivePassphraseKey(passphrase, salt, settings));
        return Seal(EnvelopeKind.Passphrase, key, kek.Bytes, salt, settings);
    }

    public static KeyEnvelope WrapWithRecoveryKey(DataKey key, RecoveryKey recovery)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var kek = new Kek(DeriveRecoveryKey(recovery, salt));
        return Seal(EnvelopeKind.RecoveryKey, key, kek.Bytes, salt, null);
    }

    [SupportedOSPlatform("windows")]
    public static KeyEnvelope WrapForThisDevice(DataKey key) =>
        new(EnvelopeKind.ThisDevice, CurrentVersion, "", "",
            Convert.ToBase64String(ProtectedData.Protect(key.Bytes.ToArray(), DeviceEntropy, DataProtectionScope.CurrentUser)));

    public static DataKey UnwrapWithPassphrase(KeyEnvelope envelope, string passphrase)
    {
        Expect(envelope, EnvelopeKind.Passphrase);
        var settings = envelope.Argon2 ?? throw new CryptographicException("The passphrase envelope has no Argon2 settings.");
        using var kek = new Kek(DerivePassphraseKey(passphrase, Convert.FromBase64String(envelope.Salt), settings));
        return Open(envelope, kek.Bytes);
    }

    public static DataKey UnwrapWithRecoveryKey(KeyEnvelope envelope, RecoveryKey recovery)
    {
        Expect(envelope, EnvelopeKind.RecoveryKey);
        using var kek = new Kek(DeriveRecoveryKey(recovery, Convert.FromBase64String(envelope.Salt)));
        return Open(envelope, kek.Bytes);
    }

    [SupportedOSPlatform("windows")]
    public static DataKey UnwrapForThisDevice(KeyEnvelope envelope)
    {
        Expect(envelope, EnvelopeKind.ThisDevice);
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Ciphertext), DeviceEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return DataKey.FromBytes(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (CryptographicException)
        {
            // A device envelope from another PC or Windows account can't be opened here.
            throw new WrongSecretException();
        }
    }

    // Passphrases are normalized (NFC) and encoded as UTF-8, so the same text typed
    // on different keyboards or platforms derives the same key.
    private static byte[] DerivePassphraseKey(string passphrase, byte[] salt, Argon2Settings settings)
    {
        var parameters = new Argon2Parameters
        {
            MemorySize = settings.MemoryKiB,
            NumberOfPasses = settings.Passes,
            DegreeOfParallelism = settings.Parallelism,
        };
        var bytes = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            return PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(bytes, salt, DataKey.Size);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // A recovery key already carries 160 random bits, so HKDF is enough.
    private static byte[] DeriveRecoveryKey(RecoveryKey recovery, byte[] salt)
    {
        var kek = new byte[DataKey.Size];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery.Bytes, kek, salt, "mossmint/recovery/v1"u8);
        return kek;
    }

    private static KeyEnvelope Seal(EnvelopeKind kind, DataKey key, ReadOnlySpan<byte> kek, byte[] salt, Argon2Settings? settings)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var sealedKey = new byte[DataKey.Size + TagSize];
        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, key.Bytes, sealedKey.AsSpan(0, DataKey.Size), sealedKey.AsSpan(DataKey.Size), Aad(kind, CurrentVersion));
        return new KeyEnvelope(kind, CurrentVersion, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(sealedKey), settings);
    }

    private static DataKey Open(KeyEnvelope envelope, ReadOnlySpan<byte> kek)
    {
        var sealedKey = Convert.FromBase64String(envelope.Ciphertext);
        if (sealedKey.Length != DataKey.Size + TagSize)
        {
            throw new CryptographicException("The key envelope is damaged.");
        }

        var plain = new byte[DataKey.Size];
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), sealedKey.AsSpan(0, DataKey.Size), sealedKey.AsSpan(DataKey.Size), plain, Aad(envelope.Kind, envelope.Version));
            return DataKey.FromBytes(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongSecretException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // Binds the ciphertext to its envelope kind and format version.
    private static byte[] Aad(EnvelopeKind kind, int version) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"mossmint/envelope/v{version}/{kind}"));

    private static void Expect(KeyEnvelope envelope, EnvelopeKind kind)
    {
        if (envelope.Kind != kind)
        {
            throw new ArgumentException($"Expected a {kind} envelope, got {envelope.Kind}.", nameof(envelope));
        }

        if (envelope.Version != CurrentVersion)
        {
            throw new CryptographicException($"Key envelope version {envelope.Version} isn't supported by this version of Mossmint.");
        }
    }

    private sealed class Kek(byte[] bytes) : IDisposable
    {
        public ReadOnlySpan<byte> Bytes => bytes;

        public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
    }
}
