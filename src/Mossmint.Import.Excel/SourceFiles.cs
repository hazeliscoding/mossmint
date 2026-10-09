using System.Security.Cryptography;
using Mossmint.Application.Import;

namespace Mossmint.Import.Excel;

// The only code that touches a source file on disk. It opens the file for
// reading, takes its bytes in one go, and never holds it open: everything
// after that works on the copy.
public static class SourceFiles
{
    // Workbooks this size are already far beyond a household budget.
    public const long MaxBytes = 64L * 1024 * 1024;

    public static SourceFile Read(string path)
    {
        // ReadWrite sharing lets this succeed while the spreadsheet app has the
        // file open; access stays read-only.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes)
        {
            throw new IOException($"{Path.GetFileName(path)} is larger than Mossmint will import.");
        }

        var content = new byte[stream.Length];
        stream.ReadExactly(content);
        return new SourceFile(Path.GetFileName(path), content);
    }

    // Re-reads the file and compares its hash with the one taken at import, so
    // the import report can show the original is untouched.
    public static bool IsUnchanged(string path, string sha256)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == sha256;
    }
}
