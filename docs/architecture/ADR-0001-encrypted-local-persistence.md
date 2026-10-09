# ADR-0001: Encrypted local persistence

- **Status:** Accepted for the approach; the encryption library is decided by the M2 spike.
- **Date:** 2026-10-09

## Context

Mossmint stores a person's complete financial picture on their own PC. It has no server to fall back on, so the local file is the only copy that matters, and anyone who copies it (a thief, a sync tool, another account on the machine) must not be able to read it. Real data must not be stored until encryption works, and the app must never quietly fall back to a plaintext database.

EF Core 10 and `Microsoft.Data.Sqlite.Core` 10 still sit on SQLitePCLRaw 2.1.x. The plain SQLite bundle has moved on to 3.x, but the SQLCipher bundle (`SQLitePCLRaw.bundle_e_sqlcipher`) stopped at 2.1.11, and its maintainer points users to SQLite3 Multiple Ciphers instead. Zetetic's official SQLCipher packages for .NET are commercial.

## Decision

1. **SQLite through EF Core 10**, one database file per profile, with foreign keys on and every multi-row change in a transaction.
2. **Page-level encryption from a SQLite build with a cipher**, chosen in the M2 spike from:
   - SQLite3 Multiple Ciphers (MIT), via `SQLitePCLRaw.bundle_e_sqlite3mc` or `SQLite3MC.PCLRaw.bundle`. It can use the SQLCipher v4 format or ChaCha20-Poly1305.
   - `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 (SQLCipher community build, no longer updated).

   The spike must show, in an **installed Velopack build**, that the database opens with the key, that a plain `sqlite3` client and a hex dump can't read it, that the default `e_sqlite3` binary isn't also shipped, and that the cipher is active at startup. Without that proof no real data is imported.
3. **Fail closed.** At open, Mossmint checks that the cipher is active (`PRAGMA cipher` / `cipher_version`) and that the key was accepted. If either fails it refuses to open the profile and says why. There is no plaintext mode.
4. **Envelope keys.** Each profile has a random 256-bit data key. The database is keyed with it as a raw key. The data key is stored only in wrapped form, in `profile.json` beside the database:
   - wrapped by a key derived from the **passphrase** with Argon2id (versioned parameters, starting at 64 MiB, 3 iterations, parallelism 1, tuned on low-end hardware in M2);
   - wrapped by a **recovery key** (random, shown once as groups of characters to print or store; never saved by Mossmint);
   - optionally wrapped with **Windows DPAPI** (current user) for convenience unlock on this PC.

   Wrapping uses AES-256-GCM with a fresh nonce. Changing the passphrase rewraps the data key without re-encrypting the database.
5. **Location.** `%LOCALAPPDATA%\Mossmint\Profiles\<profile-id>\` holds `profile.db`, `profile.json` (schema version and wrapped keys, no secrets in clear) and local backups. Velopack installs to `%LOCALAPPDATA%\Mossmint.Desktop\`, a different folder, so updates never touch profiles. The layout carries a version number.
6. **Backups are portable.** A backup is a versioned container: a consistent SQLite snapshot (online backup API, so WAL content is included) plus metadata, encrypted with AES-256-GCM under a key derived with Argon2id from the passphrase or recovery key. DPAPI is never the only way into a backup. Restore always goes into a new temporary profile, is verified, and only then becomes visible.
7. **Migrations** run inside a transaction after an automatic local backup. A failed migration rolls back and leaves the previous file in place.

## Consequences

- Losing both the passphrase and the recovery key means the data can't be recovered. The UI says so plainly when the profile is created.
- DPAPI convenience unlock protects against other users and offline copying, not against malware in the same Windows session (see the threat model).
- The SQLite build is native code that Mossmint ships, so its version and checksum are part of the release checklist.
- Tests need a real encrypted database: a check that a plain client can't read it, and wrong-key, corrupt-file and truncated-backup cases.

## Open questions for M2

- Which library, and which cipher scheme (SQLCipher v4 format for tool compatibility, or ChaCha20-Poly1305).
- Argon2 implementation: a maintained, permissively licensed .NET package, or `libsodium` bindings.
- Recovery key format and how it's checked when entered.
