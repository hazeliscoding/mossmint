using Microsoft.Data.Sqlite;
using Mossmint.Persistence.Security;

namespace Mossmint.Persistence.Sqlite;

// The profile can't be opened safely: the cipher isn't active, or the wrong
// SQLite build was loaded. Mossmint refuses rather than continuing in plaintext.
public sealed class EncryptionUnavailableException(string message) : InvalidOperationException(message);

// Opens profile databases encrypted with SQLite3 Multiple Ciphers in SQLCipher
// v4 format (ADR-0001), and fails closed if anything about that isn't true.
public static class EncryptedSqlite
{
    private const string ExpectedProvider = "sqlite3mc";

    private static readonly Lazy<string> Provider = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return SQLitePCL.raw.GetNativeLibraryName();
    });

    // Opens (or creates) the database at path, keyed with the data key. Throws
    // WrongSecretException when the key doesn't open an existing file.
    public static SqliteConnection Open(string path, DataKey key, bool create = false)
    {
        EnsureCipherBuild();
        if (!create && !File.Exists(path))
        {
            throw new FileNotFoundException("The profile database is missing.", Path.GetFileName(path));
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());

        try
        {
            connection.Open();

            // The cipher must be chosen before the key, and the key before any other statement.
            Execute(connection, "PRAGMA cipher = 'sqlcipher'");
            Execute(connection, "PRAGMA legacy = 4");
            Execute(connection, $"PRAGMA key = \"{key.ToSqlCipherRawKey()}\"");

            // The build is known to be sqlite3mc, so "file is not a database" (26)
            // here can only mean the key doesn't open this file.
            try
            {
                VerifyCipher(connection);
                Scalar(connection, "SELECT count(*) FROM sqlite_master");
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 26)
            {
                throw new WrongSecretException();
            }

            Execute(connection, "PRAGMA journal_mode = WAL");
            Execute(connection, "PRAGMA foreign_keys = ON");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void EnsureCipherBuild()
    {
        if (Provider.Value != ExpectedProvider)
        {
            throw new EncryptionUnavailableException(
                $"Mossmint loaded the '{Provider.Value}' SQLite build instead of '{ExpectedProvider}', so it can't encrypt profiles.");
        }
    }

    public static string? CipherVersion(SqliteConnection connection) => Scalar(connection, "SELECT sqlite3mc_version()") as string;

    private static void VerifyCipher(SqliteConnection connection)
    {
        string? version, cipher;
        try
        {
            version = CipherVersion(connection);
            cipher = Scalar(connection, "PRAGMA cipher") as string;
        }
        catch (SqliteException e) when (e.SqliteErrorCode != 26)
        {
            throw new EncryptionUnavailableException($"The encryption library didn't answer: {e.Message}");
        }

        if (version is null || !string.Equals(cipher, "sqlcipher", StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptionUnavailableException("The database cipher isn't active, so Mossmint won't open the profile.");
        }
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
