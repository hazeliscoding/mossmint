using System.Text;

namespace Mossmint.Import.Transactions;

internal static class TextDecoding
{
    public static Encoding Windows1252 { get; } = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    // Null when the bytes aren't valid UTF-8, so the caller can fall back.
    public static string? StrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
