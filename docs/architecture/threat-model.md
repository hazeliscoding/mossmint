# Threat model (draft)

- **Status:** Draft, M0. Revisit when M2 (encrypted profiles), M3 (imports) and M6 (releases) land.
- **Date:** 2026-10-09

## What we protect

| Asset | Why it matters |
|---|---|
| Profile database | Balances, debts, income, transactions: a complete financial picture |
| Key material | The data key, passphrase, recovery key and DPAPI blob unlock everything |
| Backups | Portable copies of the profile, likely stored on USB drives or cloud folders |
| Original workbooks | The user's source of truth; must never be altered |
| Forecast integrity | A wrong "safe to spend" can cause an overdraft or a missed payment |
| Release artifacts | A tampered installer compromises every user |

## Who we defend against

1. **Someone with a copy of the files**: a stolen powered-off laptop, a cloud-sync folder, another Windows account, a discarded drive.
2. **Curious software that reads logs or crash dumps** without targeting Mossmint.
3. **Supply-chain attackers**: a malicious package, a compromised GitHub Action, a tampered release.
4. **Malicious or careless contributors** adding network code, telemetry or real data.
5. **The app itself**: bugs that corrupt data, lose a backup, or report a guess as fact.

## Out of scope

- Malware or an attacker in the user's **unlocked** Windows session. They can read the screen, log keystrokes and use DPAPI as the user. Mossmint limits exposure (locked profiles, no plaintext caches) but can't defend against this.
- Physical attacks on a running, unlocked machine.

## Trust boundaries

```text
[ user ] -> [ Mossmint.Desktop process ] -> [ encrypted profile files ]  (%LOCALAPPDATA%\Mossmint)
                    |                     -> [ backup files ]              (user-chosen location)
                    |                     <- [ workbooks, read-only ]      (user-chosen files)
                    +-- (opt-in, M6+) ----> [ GitHub Releases: update check ]
                    +-- (opt-in, M7+) ----> [ connector provider or relay ]
```

Nothing crosses the network boundary unless the user turned that feature on.

## Threats and mitigations

| Threat | Mitigation | Where |
|---|---|---|
| Profile file copied and read | Page-level encryption, envelope keys, fail-closed open | ADR-0001, M2 |
| Weak passphrase brute-forced offline | Argon2id with tuned memory cost; recovery key is high-entropy | ADR-0001 |
| Plaintext fallback after a packaging mistake | Startup cipher check refuses to open; installed-build test | ADR-0001, M2, M6 |
| Backup restored over the wrong profile | Restore into a new temporary profile, verify, then switch; never overwrite silently | ADR-0001, M2 |
| Financial data in logs or crash output | No crash upload; redacting logger; no values, paths or tokens logged | AGENTS.md, M2 |
| Network exfiltration by new code | Architecture test rejects networking assemblies; runtime monitor in smoke tests; review rule | M0 |
| Real data committed to the public repo | `.gitignore`, CI private-file guard with positive control, secret scan of full history | M0 |
| Import modifies or corrupts the original workbook | Read-only open, SHA-256 before and after, no writes to source paths | M3 |
| Failed import leaves a half-written profile | Snapshot, then one atomic transaction | M3 |
| Forecast reports a guess as fact | Provenance on every figure; *needs confirmation* and *never pays off* results | ADR-0002, M1 |
| Malicious dependency or action | Central version pins, lock files, NuGet audit, Dependabot, actions pinned by SHA, checksummed tools | M0 |
| Tampered installer | SHA-256 checksums now; code signing and signed update feed before public release | M6 |
| Connector credential leak | Stored only in the encrypted profile, redacted everywhere, revocable | Connector ADR, M7 |

## Open items

- Choose and verify the encryption library (M2 spike).
- Decide code-signing approach for Windows releases (M6).
- Decide whether profiles auto-lock after idle time, and the default.
