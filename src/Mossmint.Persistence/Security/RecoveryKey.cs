using System.Security.Cryptography;
using System.Text;

namespace Mossmint.Persistence.Security;

// 160 random bits shown once as 32 Crockford base32 characters in groups of
// four (ADR-0001). Parsing forgives case, dashes, spaces and the look-alikes
// I/L (read as 1) and O (read as 0). A mistyped key fails to unwrap.
public sealed class RecoveryKey
{
    private const int ByteCount = 20;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly byte[] _bytes;

    private RecoveryKey(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static RecoveryKey Generate() => new(RandomNumberGenerator.GetBytes(ByteCount));

    public static bool TryParse(string? text, out RecoveryKey? key)
    {
        key = null;
        if (text is null)
        {
            return false;
        }

        var clean = new StringBuilder(32);
        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                var other => other,
            };
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }

            clean.Append(c);
        }

        if (clean.Length != 32)
        {
            return false;
        }

        var bytes = new byte[ByteCount];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in clean.ToString())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        key = new RecoveryKey(bytes);
        return true;
    }

    public override string ToString()
    {
        var text = new StringBuilder(39);
        int buffer = 0, bits = 0, written = 0;
        foreach (var b in _bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written > 0 && written % 4 == 0)
                {
                    text.Append('-');
                }

                text.Append(Alphabet[(buffer >> bits) & 31]);
                buffer &= (1 << bits) - 1;
                written++;
            }
        }

        return text.ToString();
    }
}
