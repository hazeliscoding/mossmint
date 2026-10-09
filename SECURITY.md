# Security policy

Mossmint holds personal financial data on the user's own computer. Security reports are welcome and taken seriously.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting: **Security → Report a vulnerability** on this repository. Please don't open a public issue for a vulnerability.

Include what you found, how to reproduce it, and what an attacker gains. **Never include real financial data**, real account numbers or real credentials in a report; describe the shape of the data instead, or use synthetic values.

You can expect an acknowledgement within a week. Fixes for confirmed issues ship in the next release, with credit if you want it.

## Supported versions

Mossmint is pre-release. Only the latest commit on `main` and the latest published release receive fixes.

## Scope

In scope:

- Reading a locked profile, backup or its keys without the passphrase or recovery key.
- Financial data, file paths or secrets reaching logs, crash output, screenshots, exports or the network without the user's explicit action.
- Any network traffic during normal offline use.
- An import that modifies the original workbook, or a failed import that corrupts an existing profile.
- Update or installer tampering, and dependency or build-pipeline compromise.
- A forecast or payoff result presented as confirmed when its inputs are estimates or stale.

Out of scope:

- An attacker who already controls the user's unlocked Windows session (see the [threat model](docs/architecture/threat-model.md)). Mossmint reduces exposure there but can't prevent it.
- Physical attacks on unlocked, running hardware.
- Reports that only show a missing hardening header or similar, with no concrete impact.

## How Mossmint protects data

- Profiles are encrypted at rest before any real data is stored ([ADR-0001](docs/architecture/ADR-0001-encrypted-local-persistence.md)).
- No telemetry, analytics or crash upload. No network calls in normal use; CI and tests enforce this.
- Release artifacts are published with SHA-256 checksums. Code signing is planned for the first public release.
