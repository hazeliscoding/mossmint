# Roadmap

Mossmint is a local-first Windows desktop app (C#, .NET 10, Avalonia) for paycheck forecasting and debt payoff planning. This file tracks what gets built, in what order, and the decisions already made. It is also the status file: tick items here as they land.

**Next up:** M1. Money and source labels, then schedule rules, then the installment loan model.

## Decisions (2026-10-09)

- **License: MIT.** Bundled fonts are OFL (Manrope, IBM Plex Mono), shipped with their license files.
- **Priorities, in order:** financial correctness, privacy and security, recoverability, accessible desktop UX, performance, extensibility, convenience.
- **Stack:** .NET 10 LTS (SDK pinned in `global.json`), Avalonia 12.1, CommunityToolkit.Mvvm 8.4, Microsoft.Extensions DI. Versions are pinned centrally in `Directory.Packages.props`. The design notes target Avalonia 11; 12.1 is the current stable line and is used instead.
- **Platform:** Windows 10 22H2 and Windows 11, x64. Projects avoid WPF/WinForms so macOS and Linux stay possible later.
- **Layering:** `Desktop → Application → Domain + Engine`; infrastructure (`Persistence`, `Import.*`, `Connectors.Abstractions`) implements Application ports. `Engine` references only `Domain`; `Domain` references nothing. An architecture test enforces it.
- **Money** is a `decimal` value object with an ISO 4217 currency. Mixing currencies throws. Settled amounts are stored as integer minor units; accrual keeps full precision until the model's documented rounding step. No `double` in any money path.
- **Dates:** `DateOnly` for ledger, due, pay and interest dates; `DateTimeOffset` for audit instants.
- **Reference loan model:** daily simple interest, Actual/365 by default (Actual/360 and lender-defined per loan). Payments go to fees, then interest, then principal unless the contract says otherwise. The credit card model is labelled an approximation until statements confirm it.
- **The engine never invents certainty.** A payoff that can't converge returns *never pays off*, not a date. Safe to spend returns *needs confirmation* with the missing evidence when the opening balance is stale or a due bill has no amount.
- **Tests:** xUnit v3, AwesomeAssertions (Apache-2.0; FluentAssertions 8 needs a commercial license) and FsCheck for property tests. Fixtures are synthetic and built in code.
- **Private data never enters the repo.** `.gitignore` blocks spreadsheets and databases, and CI fails if one is tracked. Real workbooks are only ever read by local Mossmint code, after the owner approves it, read-only. They are never opened by an AI tool or uploaded anywhere.
- **Public files don't name the private user, their lenders or their workbook files.** The workbook layout is described generically until the owner confirms which sheet names may appear in importer code (M3).
- **Encrypted storage comes before any real data.** `SQLitePCLRaw.bundle_e_sqlcipher` stopped at 2.1.11 while SQLitePCLRaw moved to 3.x, so M2 opens with a spike: SQLCipher bundle vs SQLite3 Multiple Ciphers. ADR-0001 has the criteria. There is never a silent plaintext fallback.
- **Status lives in this file.** It replaces the handoff's `implementation-status.md` and `START-HERE.md`. ADRs live in `docs/architecture/` and `docs/connectors/`.
- **Design:** the Mossmint mockup from Claude Design (Quorum component vocabulary, moss palette, dark first, light re-tuned). Notes are in `docs/design/`. The Quorum JS bundle isn't vendored, because its license isn't confirmed; views are rebuilt natively in Avalonia.
- **Brand:** a sprout rising from a coin circle, single colour, on a 16px grid. App icons put the moss mark on a dark canvas tile. Assets are in `docs/brand/`.
- **Screenshots** are rendered headlessly through Avalonia's Skia renderer, from synthetic data only. The sample profile is called *Juniper*.
- **Updates:** Velopack with pack ID `Mossmint.Desktop`. Update checks are opt-in and manual-first; full offline use never depends on them.
- **Profile data** lives under `%LOCALAPPDATA%\Mossmint\Profiles\<profile-id>\`, never inside the Velopack install folder.
- **Connectors:** abstractions and fakes only until after the MVP. No Plaid, bank logins or payments in the MVP.
- **Progress posts** go to the project Discord channel when a milestone starts or finishes, with a synthetic screenshot.
- **Tests run on Microsoft.Testing.Platform** (opted in through `global.json`). xUnit stays on 3.2.x because Avalonia.Headless.XUnit 12.1 fails test discovery on 4.x. FsCheck is used through its core API rather than its xUnit 4 integration.
- **Owner approval (2026-10-09):** agents may read the private workbooks in the working tree to shape the domain model and the importer. Their values still never enter the repo, docs, commits, tests, screenshots or Discord.
- **The handoff DB** for the private user is a real encrypted Mossmint profile, produced by running the M3 importer locally on the workbooks. There is no interim export format.

## M0: Skeleton and guardrails

- [x] Repo files: `README`, `ROADMAP`, `LICENSE`, `AGENTS.md`, `CLAUDE.md`, `.editorconfig`, `.gitattributes`, `.gitignore`.
- [ ] Brand: mark SVGs, plus app icon PNGs and `.ico` generated from the mark.
- [x] Solution: `Mossmint.slnx`, 8 `src` projects, `global.json`, `Directory.Build.props` (nullable, warnings as errors, lock files), `Directory.Packages.props`. Test projects arrive with the code they test.
- [x] Architecture test for the layering rule, with a positive control.
- [x] Avalonia shell that boots offline: custom title bar, sidebar with every destination, top bar with an *Offline* status, Welcome screen for a clean install, dark and light theme tokens, bundled fonts.
- [x] Headless smoke tests: the window opens, every destination navigates, nothing touches the network.
- [x] Screenshot tool: renders screens headlessly to PNG.
- [ ] Velopack: startup hook, `vpk` as a local tool, a pack script, and a proof-of-concept installer built from synthetic data.
- [ ] CI on `windows-latest`: restore, format check, build, test, vulnerable-package check, secret scan, and a private-file guard.
- [ ] `SECURITY.md`, `PRIVACY.md`, `CONTRIBUTING.md`.
- [ ] ADRs: encrypted local persistence, forecasting semantics, connector provider security. A draft threat model.

**Done when:** CI is green, the app launches on Windows with the network off, the architecture test fails on a deliberate bad reference, and `vpk pack` produces a working `Setup.exe`.

## M1: Core financial engine

- [ ] `Money`, `Currency`, rounding policy, with property tests.
- [ ] Source and quality labels on every figure: confirmed, scheduled, estimated, imported, needs review; each with an as-of date.
- [ ] Immutable domain records: account, balance snapshot, transaction, transfer group, recurring bill, income schedule, savings goal, reserve floor, debt account, APR period, planned payment.
- [ ] Schedule rules: weekly, biweekly, semi-monthly (including 15th and last day), monthly, last business day, weekend and holiday adjustment, dated overrides.
- [ ] Installment loan, daily simple interest: day-count conventions, allocation order, irregular dates, extra principal, prepayment fee, final-payment clamp, *never pays off*.
- [ ] Amortizing installment loan: periodic rate, rate changes.
- [ ] Credit card approximation: statement cycles, grace period, APR buckets, promo expiry, minimum payment; always labelled *approximate*.
- [ ] Cash-flow simulator: deterministic daily events, lowest cash between paychecks, reserve floor, transfers that net to zero, card payments that aren't counted as spending twice, negative-cash warnings.
- [ ] Paycheck funding: each bill is assigned to exactly one paycheck; the savings contribution target and the cash reserve floor are separate constraints.
- [ ] Safe to spend: an amount, or *needs confirmation* with reasons.
- [ ] Spending scenarios: base, cautious and stress, from user-entered ranges, labelled as assumptions.
- [ ] Replay: engine inputs and outputs serialize to JSON and replay identically.
- [ ] Hand-derived oracle fixtures and invariant property tests.
- [ ] `docs/financial-models/`: interest, rounding and event-ordering rules.

**Done when:** every invariant and fixture passes, the same input always gives the same output, and no input can produce a payoff date it doesn't support.

## M2: Encrypted profiles

- [ ] Spike: SQLCipher bundle vs SQLite3 Multiple Ciphers on .NET 10 + EF Core 10, tested in an installed build. Record the result in ADR-0001.
- [ ] Encrypted SQLite profile database, EF Core migrations, foreign keys on, backup before every schema change.
- [ ] Unlock: passphrase plus a recovery key; optional DPAPI convenience unlock; portable backups that don't depend on DPAPI.
- [ ] Multiple isolated profiles under `%LOCALAPPDATA%\Mossmint\Profiles\`.
- [ ] Encrypted backup container (Argon2id + AES-GCM, versioned), restore into a temporary profile and verify before reporting success.
- [ ] Manual entry of accounts, bills, income and debts.

**Done when:** a plain SQLite client can't read a profile, wrong-password and corrupt-backup cases fail safely, and a restored backup matches its source.

## M3: Workbook import and reconciliation

- [ ] Read-only pipeline: workbook reader → raw records with cell provenance → versioned mapper → draft → validation and reconciliation → one atomic commit after a snapshot.
- [ ] Dry-run report, review queue, reimport diff, SHA-256 of each source file.
- [ ] Synthetic workbook generator for the five known workbook shapes (a master budget workbook and four per-debt workbooks), including formulas, stale cached values, Google-only formulas, date serials and duplicates.
- [ ] Mismatches (balances that differ from the lender, due day vs planned day) stay review items, never silent corrections.
- [ ] Handoff DB: run the importer locally on the real workbooks to produce the private user's encrypted profile, with every unresolved mismatch listed for review.

**Done when:** synthetic imports pass, a failed import leaves the profile untouched, and the handoff DB opens in Mossmint with its review queue intact.

## M4: Desktop dashboard and daily flows

- [ ] Overview, Cash Flow, Paychecks, Debts, Bills, Savings, Transactions, following the mockup.
- [ ] Manual ledger entry, mark paid with undo, balance reconciliation.
- [ ] Every chart has a table twin; keyboard-only paths; 125–200% scaling; dark, light and high contrast.
- [ ] Generic onboarding for a user with no spreadsheets.

**Done when:** a new user can onboard with no spreadsheet, an imported user can trace every record to its source, and no screen needs the network.

## M5: Scenarios and debt planning

- [ ] Payoff comparison: avalanche, snowball, custom.
- [ ] Largest feasible extra payment over the whole horizon, revalidated against daily cash.
- [ ] Stress scenarios and a debt-free date range.
- [ ] Apply to plan with a change summary; scenarios never change the real plan silently.

**Done when:** an extra payment is never shown as safe if the forecast under the chosen assumption drops below the cushion.

## M6: Windows release

- [ ] Velopack installer, release CI on tags, checksums, signing if practical.
- [ ] Upgrade test: install, create a profile, back up, update, restore.
- [ ] User docs and a sample-data generator.

**Done when:** clean install → new or imported profile → backup → update → restore passes, and the public repo holds no private financial content.

## M7: Optional connectors (after the MVP)

- [ ] CSV with column mapping; OFX/QFX if feasible.
- [ ] SimpleFIN adapter.
- [ ] Plaid relay, only if demand justifies it, behind its own ADR and threat model.

**Done when:** every offline workflow still works with any connector unconfigured, stale or offline.

## Later

- Seasonal spending estimates and backtesting, once enough categorized history exists.
- macOS and Linux builds.
- Unencrypted JSON/CSV export, with an explicit warning.

## Not planned

- Angular, an ASP.NET server, or any mandatory cloud backend.
- Bank credential login forms, scraping bank portals, or automatic transfers and payments.
- LLM-driven financial decisions.
- Telemetry, analytics or crash upload.
- A third-party executable plugin system.
