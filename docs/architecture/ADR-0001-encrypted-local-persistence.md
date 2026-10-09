# ADR-0001: Encrypted local persistence

- **Status:** Accepted. The M2 spike chose the library and cipher scheme (below).
- **Date:** 2026-10-09, updated after the M2 spike the same day

## Context

Mossmint stores a person's complete financial picture on their own PC. It has no server to fall back on, so the local file is the only copy that matters, and anyone who copies it (a thief, a sync tool, another account on the machine) must not be able to read it. Real data must not be stored until encryption works, and the app must never quietly fall back to a plaintext database.

EF Core 10 and `Microsoft.Data.Sqlite.Core` 10 still sit on SQLitePCLRaw 2.1.x. The plain SQLite bundle has moved on to 3.x, but the SQLCipher bundle (`SQLitePCLRaw.bundle_e_sqlcipher`) stopped at 2.1.11, and its maintainer points users to SQLite3 Multiple Ciphers instead. Zetetic's official SQLCipher packages for .NET are commercial.

## Decision

1. **SQLite through EF Core 10**, one database file per profile, with foreign keys on and every multi-row change in a transaction.
2. **SQLite3 Multiple Ciphers 2.4.0 (`SQLite3MC.PCLRaw.bundle`, MIT), writing the SQLCipher v4 file format** (`cipher = 'sqlcipher'`, `legacy = 4`) with the profile's 256-bit data key passed as a raw key (`x'…'`), so no second key-derivation step runs on open.

   The spike (self-contained win-x64 publish, EF Core 10) compared three builds:

   | | `bundle_e_sqlite3mc` 2.1.11 | **`SQLite3MC.PCLRaw.bundle` 2.4.0** | `bundle_e_sqlcipher` 2.1.11 |
   |---|---|---|---|
   | SQLite inside | 3.49.1 | **3.53.4** | 3.39.2 (2022) |
   | Cipher library | sqlite3mc 2.1.0 | **sqlite3mc 2.4.0** (July 2026) | SQLCipher 4.5.2 community |
   | EF Core 10 create, write, reopen | yes | **yes** | yes |
   | No `SQLite format 3` header, no plaintext canary in DB or WAL | yes | **yes** | yes |
   | Wrong key / no key / Python's plain `sqlite3` refuse it | yes | **yes** | yes |
   | Online backup API stays encrypted and readable | yes | **yes** | yes |
   | Rekey in WAL mode | fails | **yes** | yes |
   | Reopen time (raw key) | ~120 ms | **~40 ms** | ~720 ms (passphrase KDF) |
   | Native SQLite binaries shipped | one | **one (`sqlite3mc.dll`)** | one |

   A file written by sqlite3mc 2.4.0 in SQLCipher v4 format with a raw key was read back by the independent SQLCipher 4.5.2 build, so a user can open their own data in standard SQLCipher tools with the key. That keeps their data independent of Mossmint.

   **Known risk:** the bundle depends on SQLitePCLRaw.core 3.0.2, while `Microsoft.Data.Sqlite.Core` 10 was built against 2.1.12. The spike and Mossmint's persistence tests run cleanly on it; versions are pinned, and this is re-checked when EF Core 11 (on SQLitePCLRaw 3) ships.

   The remaining proof (the packaged Velopack build opens an encrypted profile and refuses plaintext) runs through the app's `--verify-encryption` self-check against the packaged build. Without it no real data is imported.
3. **Fail closed.** At open, Mossmint runs the cipher pragmas, keys the connection, then checks `sqlite3mc_version()` and `PRAGMA cipher` report sqlite3mc in SQLCipher mode, and that the key was accepted (a read of `sqlite_master` succeeds). If any check fails it refuses to open the profile and says why. There is no plaintext mode.
4. **Envelope keys.** Each profile has a random 256-bit data key. The database is keyed with it as a raw key. The data key is stored only in wrapped form, in `profile.json` beside the database:
   - wrapped by a key derived from the **passphrase** with Argon2id via libsodium (`NSec.Cryptography`, MIT; libsodium, ISC), with versioned parameters starting at 64 MiB, 3 iterations, parallelism 1;
   - wrapped by a key derived with HKDF-SHA256 from a **recovery key**: 160 random bits shown once as 32 Crockford base32 characters in groups of four (letters that look alike are read as the same character, dashes and spaces are ignored), never saved by Mossmint. A mistyped key simply fails to unwrap;
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

## Resolved in M2

- Library and scheme: SQLite3 Multiple Ciphers 2.4.0 in SQLCipher v4 format, raw key.
- Argon2: libsodium through NSec (audited, constant-time, fast); a pure managed implementation would be slower for the same cost.
- Recovery key: 160-bit Crockford base32, checked by unwrapping.
