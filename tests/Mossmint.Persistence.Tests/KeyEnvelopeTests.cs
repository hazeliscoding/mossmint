using System.Security.Cryptography;
using Mossmint.Persistence.Security;

namespace Mossmint.Persistence.Tests;

public class KeyEnvelopeTests
{
    // Cheap enough for tests; production uses Argon2Settings.Default (64 MiB, 3 passes).
    internal static readonly Argon2Settings Fast = new(1024, 1, 1);

    [Fact]
    public void A_passphrase_envelope_unwraps_with_the_same_passphrase_only()
    {
        using var key = DataKey.Generate();
        var envelope = KeyEnvelopes.WrapWithPassphrase(key, "correct horse battery staple", Fast);

        using var opened = KeyEnvelopes.UnwrapWithPassphrase(envelope, "correct horse battery staple");
        var wrong = () => KeyEnvelopes.UnwrapWithPassphrase(envelope, "correct horse battery stapler");

        opened.Bytes.SequenceEqual(key.Bytes).Should().BeTrue();
        wrong.Should().Throw<WrongSecretException>();
    }

    [Fact]
    public void An_envelope_never_contains_the_key_or_the_passphrase()
    {
        using var key = DataKey.Generate();
        var envelope = KeyEnvelopes.WrapWithPassphrase(key, "sunflower-meadow-42", Fast);
        var stored = System.Text.Json.JsonSerializer.Serialize(envelope);

        stored.Should().NotContain(Convert.ToBase64String(key.Bytes)).And.NotContain(Convert.ToHexString(key.Bytes)).And.NotContain("sunflower");
        envelope.Argon2.Should().Be(Fast);
    }

    [Fact]
    public void Composed_and_decomposed_accents_derive_the_same_key()
    {
        using var key = DataKey.Generate();
        var composed = "café crème brûlée";
        var decomposed = "café crème brûlée";
        var envelope = KeyEnvelopes.WrapWithPassphrase(key, composed, Fast);

        using var opened = KeyEnvelopes.UnwrapWithPassphrase(envelope, decomposed);

        opened.Bytes.SequenceEqual(key.Bytes).Should().BeTrue();
    }

    [Fact]
    public void Tampering_with_the_ciphertext_or_its_kind_is_detected()
    {
        using var key = DataKey.Generate();
        var envelope = KeyEnvelopes.WrapWithPassphrase(key, "orchard-lantern-77", Fast);
        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[3] ^= 0x01;

        var flipped = () => KeyEnvelopes.UnwrapWithPassphrase(envelope with { Ciphertext = Convert.ToBase64String(bytes) }, "orchard-lantern-77");
        var relabelled = () => KeyEnvelopes.UnwrapWithRecoveryKey(envelope with { Kind = EnvelopeKind.RecoveryKey }, RecoveryKey.Generate());

        flipped.Should().Throw<WrongSecretException>();
        relabelled.Should().Throw<WrongSecretException>();
    }

    [Fact]
    public void Unknown_envelope_versions_are_refused_rather_than_guessed()
    {
        using var key = DataKey.Generate();
        var envelope = KeyEnvelopes.WrapWithPassphrase(key, "orchard-lantern-77", Fast) with { Version = 99 };

        var act = () => KeyEnvelopes.UnwrapWithPassphrase(envelope, "orchard-lantern-77");

        act.Should().Throw<CryptographicException>().WithMessage("*version 99*");
    }

    [Fact]
    public void A_recovery_key_unwraps_and_survives_formatting_slips()
    {
        using var key = DataKey.Generate();
        var recovery = RecoveryKey.Generate();
        var envelope = KeyEnvelopes.WrapWithRecoveryKey(key, recovery);
        var typed = recovery.ToString().ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l');

        RecoveryKey.TryParse(typed, out var parsed).Should().BeTrue();
        using var opened = KeyEnvelopes.UnwrapWithRecoveryKey(envelope, parsed!);

        opened.Bytes.SequenceEqual(key.Bytes).Should().BeTrue();
    }

    [Fact]
    public void A_recovery_key_is_32_characters_in_groups_of_four()
    {
        var text = RecoveryKey.Generate().ToString();

        text.Should().MatchRegex("^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$");
        RecoveryKey.TryParse(text, out var parsed).Should().BeTrue();
        parsed!.ToString().Should().Be(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD-EFGH")]
    [InlineData("UUUU-UUUU-UUUU-UUUU-UUUU-UUUU-UUUU-UUUU")]
    public void Malformed_recovery_keys_are_rejected(string text)
    {
        RecoveryKey.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void A_wrong_recovery_key_fails_cleanly()
    {
        using var key = DataKey.Generate();
        var envelope = KeyEnvelopes.WrapWithRecoveryKey(key, RecoveryKey.Generate());

        var act = () => KeyEnvelopes.UnwrapWithRecoveryKey(envelope, RecoveryKey.Generate());

        act.Should().Throw<WrongSecretException>();
    }

    [Fact]
    public void A_device_envelope_round_trips_for_the_current_windows_user()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI is Windows-only.");
            return;
        }

        using var key = DataKey.Generate();

        var envelope = KeyEnvelopes.WrapForThisDevice(key);
        using var opened = KeyEnvelopes.UnwrapForThisDevice(envelope);

        opened.Bytes.SequenceEqual(key.Bytes).Should().BeTrue();
    }

    [Fact]
    public void Disposing_a_data_key_zeroes_it()
    {
        var key = DataKey.Generate();
        key.Dispose();

        var read = () => key.Bytes.Length;

        read.Should().Throw<ObjectDisposedException>();
    }
}
