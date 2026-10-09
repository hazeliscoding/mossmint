# ADR: Connector provider security

- **Status:** Accepted (design only; no connector ships before the MVP)
- **Date:** 2026-10-09

## Context

Some users will eventually want balances and transactions to arrive automatically. Every way of doing that involves a third party that sees bank data: an aggregator, a bridge, or the bank's own API. Mossmint's core promise is that it works fully offline with no account and no Mossmint server. Connectors must not weaken that promise for people who never use them.

## Decision

### Connectors are optional, off by default, and outside the core

- No connector code runs, and no connector endpoint is contacted, until the user adds a connection.
- The financial engine never knows where data came from beyond its provenance labels. Connector data goes through the same normalization and reconciliation as file imports, and ambiguous matches wait for the user.
- Each provider declares its capabilities (accounts, balances, transactions, liability terms, statements, history). A provider that doesn't supply APRs is never advertised as tracking APR changes.
- Removing a connection revokes its credential where the provider allows it, deletes the stored credential, and offers to erase the data it downloaded.

### When an optional service is justified

A Mossmint-side service is justified only when a provider's terms make it impossible to hold the needed secret on the user's machine. A public OSS binary can't keep a shared application secret.

| Provider | Needs a service? | Why |
|---|---|---|
| CSV / OFX / QFX files | No | Local files only |
| SimpleFIN Bridge | No | The user claims a setup token; the resulting access URL is the user's own credential and is kept in the encrypted profile |
| Plaid | Yes | Link tokens, public-token exchange and webhooks need the Plaid client secret, which can't ship in the app |

### Rules for any relay (Plaid or similar)

- It is a separate, optional deployment. Self-hosting is a supported, documented path. Mossmint never requires a relay operated by the project.
- It holds the minimum: the provider secret and per-user item tokens, nothing else. It forwards data to the authenticated desktop client and doesn't store transaction content beyond what delivery needs.
- Desktop clients authenticate to it per installation; it is never an open mailbox for financial data.
- A project-operated relay would need its own ADR, threat model, privacy notice and cost model before it exists.

### Credentials and logs

- Connector credentials live only inside the encrypted profile and are never written to logs, crash output, diagnostics or exports.
- Bearer URLs and tokens are redacted wherever they could be displayed.
- Freshness is per observation: a sync timestamp is not a balance's timestamp. The UI shows "stale" with the last successful time rather than a green check.

### User consent

Before connecting, the user sees which company will see their bank data, what Mossmint receives, how often it refreshes, and how to disconnect and erase.

## Consequences

- MVP ships `Mossmint.Connectors.Abstractions` with fake connectors for tests, and an informational Connections page with no connect buttons.
- SimpleFIN can arrive without any infrastructure; Plaid waits for demand and a separate ADR.
- People who never connect anything lose nothing, and the app never contacts a provider for them.
