# ADR: SimpleFIN connector

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

M7 brings bank data in. Some banks offer no CSV or OFX download at all (the test user's offers only PDF statements). [SimpleFIN](https://www.simplefin.org/protocol.html) is a small read-only protocol: the user makes a one-time setup token at a SimpleFIN server, usually the SimpleFIN Bridge (a paid service run by its own company, which reaches banks through an aggregator), and an app claims the token for an access URL that reads balances and transactions. No bank login passes through the app, and no Mossmint-run service is needed ([provider security ADR](ADR-provider-security.md)).

This is the first code in Mossmint that makes a network call other than the opt-in update check.

## Decision

### Where the network code lives

- All SimpleFIN code is in `Mossmint.Connectors.SimpleFin`, the only project allowed to reference networking assemblies. `ArchitectureTests` lists it as the one exception and checks that it really uses the network, so the exception can't go stale silently. Domain, Engine, Application, Persistence and Desktop stay network-free; Desktop reaches the connector only through `IConnectorProvider` and `IConnector`.
- Nothing runs until the user connects: no request is made at startup, on unlock or on a timer. Syncing is manual, with **Sync now**.

### Connecting

1. The user pastes a setup token. Mossmint decodes it and shows the host it points to before sending anything. Only an `https` address without embedded credentials is accepted.
2. Mossmint `POST`s to that claim URL once. A `403` means the token was used or doesn't exist; the user is told to disable it at SimpleFIN if they didn't use it, as the protocol asks.
3. The answer must be an `https` access URL with credentials. It is stored only in the encrypted profile (the `Connections` table), never in logs, the audit trail, exports or error messages. A backup carries it, encrypted like everything else.

### Syncing

- `GET {access URL}/accounts?version=2&start-date=…`, with the URL's credentials sent as HTTP Basic auth, never in the request line. Redirects are not followed, cookies are off, and TLS certificates are verified by the platform.
- The first sync asks for 90 days, the Bridge's limit per request. Later syncs start 5 days before the previous one, as the Bridge advises, so late-posting transactions aren't missed; transaction IDs make the overlap harmless. A gap of more than 85 days is clamped to 90 and reported.
- Pending transactions are left out until 0.8 can link a pending charge to what posts.
- Amounts and balances arrive from the account holder's side and are turned around for cards and loans. A balance carries its own `balance-date` and counts as confirmed.
- The provider's `errlist` messages are shown per account and queued for review. Its text is stripped of control characters and capped before display.
- Each SimpleFIN account feeds a Mossmint account the user links it to, or is left out. One transaction source per account still applies.

### Failures

| Answer | Shown as |
|---|---|
| `403` | The connection was refused or disabled; connect again with a new token |
| `402` | The SimpleFIN subscription needs attention |
| Network error, timeout, other status | SimpleFIN couldn't be reached; try again |
| Unreadable body | SimpleFIN's answer couldn't be read; nothing is imported |

### Disconnecting

Disconnecting deletes the stored access URL. SimpleFIN has no revoke call, so Mossmint tells the user to remove the app's access at SimpleFIN too. What the connection imported stays, like any import, unless the user also ticks *remove the transactions and balances it brought in*; then those are deleted (the audit trail records the counts), while accounts and the import history stay.

## Consequences

- People who never connect are unaffected: no request, no credential, no new screen beyond the Connections page.
- The user pays SimpleFIN and trusts it, and its aggregator, with their bank data. Mossmint says so before connecting.
- A leaked profile backup with its passphrase exposes the access URL; that is no worse than the financial data beside it, and the user can disable the URL at SimpleFIN.
