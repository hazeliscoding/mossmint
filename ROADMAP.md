# Roadmap

Mossmint is a local-first Windows desktop app (C#, .NET 10, Avalonia) for paycheck forecasting and debt payoff planning. This file tracks what gets built, in what order, and the decisions already made. It is also the status file: tick items here as they land.

**Next up:** M4 (v0.4). The dashboard and daily flows, starting with the Overview and Cash Flow pages.

## Releases

Until 1.0, each milestone ships as its own minor version. The first release bundled M0–M3, so there was no 0.1 or 0.2.

| Version | Milestones | Status |
|---|---|---|
| [0.3.0](https://github.com/hazeliscoding/mossmint/releases/tag/v0.3.0) | M0–M3: skeleton, financial engine, encrypted profiles, workbook import | Released 2026-10-09 |
| 0.4 | M4: dashboard and daily flows | Next |
| 0.5 | M5: scenarios and debt planning | Planned |
| 0.6 | M6: Windows release (update check, export, signing, upgrade test, user docs) | Planned |
| 0.7 | M7: bank data in (CSV, OFX/QFX, SimpleFIN) | Planned |
| 0.8 | M8: matching real activity to the plan | Planned |
| 0.9 | M9: learning from history | Planned |
| 1.0 | The promise release: compatibility, audits, betas | Planned |

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
- **Private data never enters the repo.** `.gitignore` blocks spreadsheets and databases, and CI fails if one is tracked. Real workbooks are migrated only by local Mossmint code, read-only. Agents read them only with the owner's approval for a task (see below).
- **Public files don't name the private user, their lenders or their workbook files.** Sheet names and column labels may appear in importer code (owner approval, M3 below).
- **Encrypted storage comes before any real data.** EF Core 10 is still on SQLitePCLRaw 2.1.x, and `SQLitePCLRaw.bundle_e_sqlcipher` stopped at 2.1.11, so M2 opens with a spike: SQLite3 Multiple Ciphers vs the last SQLCipher bundle. ADR-0001 has the criteria and the key design. There is never a plaintext fallback.
- **Status lives in this file.** It replaces the handoff's `implementation-status.md` and `START-HERE.md`. ADRs live in `docs/architecture/` and `docs/connectors/`.
- **Design:** the Mossmint mockup from Claude Design (Quorum component vocabulary, moss palette, dark first, light re-tuned). Notes are in `docs/design/`. The Quorum JS bundle isn't vendored, because its license isn't confirmed; views are rebuilt natively in Avalonia.
- **Brand:** a sprout knocked out of a filled coin, single colour, fills only, on a 16px grid (refined from the outlined first draft, which read as a power symbol at small sizes). App icons put the light moss mark on a dark canvas tile. Assets are in `docs/brand/`.
- **Screenshots** are rendered headlessly through Avalonia's Skia renderer, from synthetic data only. The sample profile is called *Juniper*.
- **Updates:** Velopack with pack ID `Mossmint.Desktop`. Update checks are opt-in and manual-first; full offline use never depends on them.
- **Profile data** lives under `%LOCALAPPDATA%\Mossmint\Profiles\<profile-id>\`, never inside the Velopack install folder.
- **Connectors:** abstractions and fakes only until after the MVP. No Plaid, bank logins or payments in the MVP.
- **Progress posts** go to the project Discord channel when a milestone starts or finishes, with a synthetic screenshot.
- **Tests run on Microsoft.Testing.Platform** (opted in through `global.json`). xUnit stays on 3.2.x because Avalonia.Headless.XUnit 12.1 fails test discovery on 4.x. FsCheck is used through its core API rather than its xUnit 4 integration.
- **Owner approval (2026-10-09):** agents may read the private workbooks in the working tree to shape the domain model and the importer. Their values still never enter the repo, docs, commits, tests, screenshots or Discord.
- **The handoff DB** for the private user is a real encrypted Mossmint profile, produced by running the M3 importer locally on the workbooks. There is no interim export format.

## Decisions (2026-10-09, M1)

- **Interest is charged when a payment pays it.** Loan interest stays exact until a payment that pays interest charges it, rounded to the cent. A principal-only extra charges none, so an extra payment never adds a rounding step or raises total interest (property-tested).
- **Amortizing loans** round a derived level payment up to the cent, and the last payment of the term settles what per-period rounding leaves.
- **Card results are Estimated at best.** Payments follow CARD Act ordering, interest is charged at statement close, and a payment that clears the card also pays interest accrued since the last statement.
- **Within a day:** outflows, then inflows, then the savings contribution the paycheck funds. The day's low is the lowest running balance.
- **Paycheck funding carries leftovers forward** (negative when short), the way the reference spreadsheets do. The savings target is reported separately from the reserve floor.
- **Safe to spend** counts estimated bills at the top of their range; the horizon is the caller's choice (`DayBeforeNextPayday` is the usual one).
- **US Federal Reserve holidays** are the default bank calendar for paydays, as a stated assumption; users can add closed days.
- **Synthetic samples** live in `samples/Mossmint.Samples` (the *Juniper* profile), used by the screenshot tool's engine preview. Tests keep their own fixtures so sample changes can't move test expectations.

## Decisions (2026-10-09, M2)

- **Encryption:** SQLite3 Multiple Ciphers 2.4.0 writing the SQLCipher v4 format with a raw 256-bit data key (ADR-0001). It's current, MIT, reopens in about 40 ms, and standard SQLCipher tools can read a user's own data with their key. It pulls SQLitePCLRaw.core 3.x under EF Core 10; recheck when EF Core 11 ships.
- **Key envelopes** live in `profile.json`: passphrase (Argon2id via libsodium/NSec, 64 MiB, 3 passes), recovery key (160 bits, Crockford base32, HKDF), and optional DPAPI. Passphrases are NFC-normalized UTF-8. AES-256-GCM binds each wrap to its kind and version.
- **Passphrases** need at least 12 characters. "Unlock on this PC" is off by default; when it's on and there is one profile, the app unlocks at start.
- **The recovery key is shown once** and the user must confirm they stored it before continuing. Replacing it retires the old one.
- **Backups:** one `.mmbak` file, chunked AES-GCM bound to the header, with no personal metadata in clear. Every backup is test-restored before it's reported. Restores land in a new profile and never overwrite one. Local copies keep the newest five.
- **Fail closed:** a profile opens only if the cipher library is sqlite3mc, the cipher is active, and the key works. `Mossmint.Desktop.exe --verify-encryption` proves it in any build, and CI runs it.
- **`MOSSMINT_DATA_ROOT`** redirects the data folder; tests and the screenshot tool always use it, so they can't touch real profiles.
- **Balances typed in** are UserConfirmed as of the date entered. Loan and card payments become debt-payment bills, so the forecast sees them and they never count as spending.
- **Profile names show before unlock** (the unlock screen needs them); nothing else does.

## Decisions (2026-10-09, M3)

- **Owner approval:** the real workbook data may be used for the importer and the handoff profile, and the workbooks' sheet names and labels may appear in importer code. The importer recognizes a workbook by its sheets and labels, never by its file name, so file and lender names stay out of the code. Values still never enter the repo, tests, screenshots or Discord, and the `.xlsx` files stay gitignored.
- **A built-in `.xlsx` reader, no package.** It handles zip and XML only. It refuses DTDs and caps how much a file may expand to. It reads saved values, formulas, fills and number formats, and never evaluates a formula. It matched openpyxl on every cell of the real workbooks. ClosedXML and the Open XML SDK were heavier than the need.
- **Imported figures are *Imported, needs review*** until the user confirms them. Connector figures are *Connector*, with the observation's own date.
- **Re-imports match by stable keys** (`bill:mid:rent`). A record edited in Mossmint keeps the user's version. Records missing from the source stay and go to review. Findings are fingerprinted, so a dismissed item stays dismissed.
- **A balance typed over a workbook's projection** is imported as its own snapshot. If it differs from the workbook's own projection for that payment, the gap goes to review. The workbooks' "Reconciliation Difference" cells can't show this, because their projection already uses the typed balance.
- **Card due dates and minimum payment rules** aren't in the workbooks. Mossmint assumes 21 days after closing and the larger of $40 or 1% plus interest, and queues that assumption for review.
- **Budget workbook:** upcoming paycheck blocks become income and bills, and past blocks stay as raw history. The newest past left-over becomes the checking balance, flagged as budget arithmetic rather than a bank figure.
- **Each import takes an encrypted snapshot first**, and the five newest are kept.
- **Handoff profile:** produced on 2026-10-09 with `tools/Mossmint.ImportTool`, verified by restoring its backup with both secrets. It's delivered as the backup file, with the secrets sent separately.

## Decisions (2026-10-09, releases)

- **Releases follow the Prompuff process,** moved up from M6 so the handoff profile has an app to open (owner's request). The version bump lands on `main` as `chore(release): x.y.z`. A `v*` tag on that commit starts the release workflow, which tests, packs, smoke-tests the portable build, installs with `Setup.exe` on the runner, runs the encryption self-check on both, and opens a **draft** GitHub Release with `SHA256SUMS`. The owner reads the draft and publishes it.
- **Release tags can't be moved or deleted** (the `release tags` ruleset). Only `v*` tags can use the `release` environment.
- **Versions follow milestones until 1.0:** 0.3.0 is the first release, with M0–M3. It was published on 2026-10-09; the public `Mossmint-Setup.exe` matches `SHA256SUMS`. A tag with a pre-release suffix (`v0.4.0-beta.1`) packs to the `win-beta` channel and becomes a GitHub pre-release.
- **Release files:** `Mossmint-Setup.exe`, `Mossmint-win-x64-Portable.zip`, the Velopack update files and `SHA256SUMS`.
- **Signing** uses Azure Artifact Signing the way Prompuff does, and switches on once the `release` environment has the Azure variables and a federated credential trusts it. Until then builds are unsigned, and SmartScreen warns on first launch. Signing waits until later (owner's call).
- **Direct pushes to `main` are fine until 1.0.** From 1.0 on, `main` takes changes only through pull requests, protected the way Prompuff's is: no deletion or force-push, linear history, rebase merges, and CI required and up to date before merging.

## Decisions (2026-10-10, road to 1.0)

- **Bank data before 1.0:** CSV with column mapping, OFX/QFX and a SimpleFIN adapter are wanted, in 0.7. **Plaid moves after 1.0** as a stretch goal, still behind its own ADR and threat model.
- **0.8 matches real activity to the plan,** and **0.9 learns from history** (the seasonal estimates and backtesting that were under Later, plus export). Both need the transactions 0.7 brings in.
- **1.0 is the promise release:** compatibility from 1.0 on, tested upgrades from every release, audits, and betas first, the way Prompuff did it.
- **1.0 launches with a video and a landing page,** the way Prompuff did. The video is made with the `/brag` skill from Mossmint's own tokens, fonts and copy ("Make room to grow."), and shows only the synthetic *Juniper* household. The landing page is a static site on GitHub Pages that keeps the app's promise: no analytics or trackers, nothing loaded from other sites, and nothing that asks for financial data.
- **The opt-in update check moves into 0.6,** because a real user now installs releases and shouldn't have to download each one by hand.
- **Check now moves ahead of M4** (owner's request), so that user can update from Settings straight away. Check, download and restart are separate steps, and the restart waits for a running backup or import. The optional daily check stays in 0.6. [ADR-0003](docs/architecture/ADR-0003-update-check.md) lists what GitHub sees.
- **Research on user value and feasibility (2026-10-10) reshaped 0.6 to 0.9:**
  - Export moves up to 0.6. Pre-1.0 users need a way out, and the JSON export doubles as the reference for the 1.0 compatibility tests.
  - Automated accessibility checks run from 0.4 on, so the 1.0 audit confirms rather than discovers.
  - In 0.7 the bank-reported balance matters most: a fresh balance clears *needs confirmation*.
  - Until 0.8 detects duplicates, each account has one transaction source.
  - Forecasts are journaled from 0.7, so 0.9 can backtest against what was really forecast.
  - The OFX and CSV parsers are written in-house, like the `.xlsx` reader. The .NET OFX libraries haven't been maintained since 2023.
  - SimpleFIN is synced by hand: it allows 24 requests a day and 90 days per request. That's also why 0.7 imports other apps' exports, to give 0.9 a year of history.
  - Seasonal estimates wait for 13 months of history.
- **A profile or backup saved by a newer Mossmint is refused,** untouched, with a message to update (done 2026-10-10). Betas and downgrades can't misread data they don't understand.
- **Bank logins:** Mossmint never asks for them. A bank feed such as SimpleFIN is a separate service the user signs up and pays for, and it holds the bank login. There is still no Mossmint account.
- **The app icon gets depth** (owner's request): a minted coin with a sprout, on a moss tile with the macOS margin, and a simpler full-bleed drawing for 16 to 32 px. The flat single-colour mark stays for the app's own UI. Masters and the regenerate command are in `docs/brand/`.
- **M4 pages leave some mockup parts for later.** The stress slider, the late-paycheck toggle and the extra-payment comparison come with Scenarios in M5. Ledger search and duplicate flags come with bank data (0.7 and 0.8). The add-entry form's live Safe to spend preview and Paychecks' *Override a date* are under Later. Goals show a progress bar instead of a ring.
- **Reconciling keeps the difference on the balance.** The bank's figure becomes the latest balance, marked *Differs* when it doesn't match what Mossmint expected; no balancing entry is invented and nothing is deleted. Entries typed in are dated today or earlier, since money still to come belongs in Bills.
- **Savings goals don't move money.** A dated goal shows what's left divided over the paydays before its date, rounded up, and the Savings page says when the goals together need more than each paycheck sets aside. The rule is in `docs/financial-models/cash-flow.md`.
- **Mark paid records a payment entry** (M4). The forecast starts from the latest confirmed balance plus the manual entries recorded after it, so a paid bill comes off the balance at once and its due date leaves the forecast. Imported entries don't move it: an imported balance already includes them. Undo stays on the row with no time limit, instead of the mockup's 10-second toast, so nobody has to beat a timer (WCAG 2.2.1). The rules are in `docs/financial-models/cash-flow.md`.

## M0: Skeleton and guardrails (v0.3.0)

- [x] Repo files: `README`, `ROADMAP`, `LICENSE`, `AGENTS.md`, `CLAUDE.md`, `.editorconfig`, `.gitattributes`, `.gitignore`.
- [x] Brand: mark SVGs (refined for legibility: a sprout knocked out of a filled coin), plus the app icon PNG and `.ico`, generated by `tools/Mossmint.Screenshots -- --icons`.
- [x] Solution: `Mossmint.slnx`, 8 `src` projects, `global.json`, `Directory.Build.props` (nullable, warnings as errors, lock files), `Directory.Packages.props`. Test projects arrive with the code they test.
- [x] Architecture test for the layering rule, with a positive control.
- [x] Avalonia shell that boots offline: custom title bar, sidebar with every destination, top bar with an *Offline* status, Welcome screen for a clean install, dark and light theme tokens, bundled fonts.
- [x] Headless smoke tests: the window opens, every destination navigates, nothing touches the network.
- [x] Screenshot tool: renders screens headlessly to PNG.
- [x] Velopack: startup hook, `vpk` as a local tool, a pack script with a data-file guard and SHA256SUMS, and a proof-of-concept installer. The packaged app boots from the portable build; the release workflow also installs with `Setup.exe` (since 0.3.0).
- [x] CI on `windows-latest`: restore, format check, build, test, vulnerable-package check, secret scan, and a private-file guard.
- [x] `SECURITY.md`, `PRIVACY.md`, `CONTRIBUTING.md`; private vulnerability reporting is on.
- [x] ADRs: encrypted local persistence, forecasting semantics, connector provider security. A draft threat model.

**Done when:** CI is green, the app launches on Windows with the network off, the architecture test fails on a deliberate bad reference, and `vpk pack` produces a working `Setup.exe`.

## M1: Core financial engine (v0.3.0)

- [x] `Money`, `Currency`, rounding policy, with property tests.
- [x] Source and quality labels on every figure: confirmed, scheduled, estimated, imported, needs review; each with an as-of date.
- [x] Immutable domain records: account, balance snapshot, transaction, transfer group, recurring bill, income schedule, savings goal, reserve floor, debt account, APR period, planned payment.
- [x] Schedule rules: weekly, biweekly, semi-monthly (including 15th and last day), monthly, last business day, weekend and holiday adjustment, dated overrides.
- [x] Installment loan, daily simple interest: day-count conventions, allocation order, irregular dates, extra principal, prepayment fee, final-payment clamp, *never pays off*.
- [x] Amortizing installment loan: periodic rate, rate changes.
- [x] Credit card approximation: statement cycles, grace period, APR buckets, promo expiry, minimum payment; always labelled *approximate*.
- [x] Cash-flow simulator: deterministic daily events, lowest cash between paychecks, reserve floor, transfers that net to zero, card payments that aren't counted as spending twice, negative-cash warnings.
- [x] Paycheck funding: each bill is assigned to exactly one paycheck; the savings contribution target and the cash reserve floor are separate constraints.
- [x] Safe to spend: an amount, or *needs confirmation* with reasons.
- [x] Spending scenarios: base, cautious and stress, from user-entered ranges, labelled as assumptions.
- [x] Replay: engine inputs and outputs serialize to JSON and replay identically.
- [x] Hand-derived oracle fixtures and invariant property tests.
- [x] `docs/financial-models/`: interest, rounding and event-ordering rules.

**Done when:** every invariant and fixture passes, the same input always gives the same output, and no input can produce a payoff date it doesn't support.

## M2: Encrypted profiles (v0.3.0)

- [x] Spike: SQLCipher bundle vs SQLite3 Multiple Ciphers on .NET 10 + EF Core 10. SQLite3 Multiple Ciphers 2.4.0 won; results are in ADR-0001. The packaged Velopack build passes `--verify-encryption`.
- [x] Encrypted SQLite profile database, EF Core migrations, foreign keys on, an encrypted snapshot before every schema change.
- [x] Unlock: passphrase plus a recovery key; optional DPAPI convenience unlock; portable backups that don't depend on DPAPI.
- [x] Multiple isolated profiles under `%LOCALAPPDATA%\Mossmint\Profiles\`.
- [x] Encrypted backup container (versioned, chunked AES-GCM), test-restored before reporting success; restore lands in a new profile.
- [x] Manual entry of accounts, bills, income and debts (the Start fresh setup).

**Done when:** a plain SQLite client can't read a profile, wrong-password and corrupt-backup cases fail safely, and a restored backup matches its source.

## M3: Workbook import and reconciliation (v0.3.0)

- [x] Read-only pipeline: workbook reader → raw records with cell provenance → versioned mapper → draft → validation and reconciliation → one atomic commit after a snapshot.
- [x] Dry-run report, review queue, reimport diff, SHA-256 of each source file.
- [x] Synthetic workbook generator for the five known workbook shapes (a master budget workbook and four per-debt workbooks), including formulas, stale cached values, Google-only formulas, date serials and duplicates.
- [x] Mismatches (balances that differ from the lender, due day vs planned day) stay review items, never silent corrections.
- [x] Connector contract (`Mossmint.Connectors.Abstractions`) and fake connectors that feed the same normalization and reconciliation pipeline, with replay, tombstone and stale-data tests.
- [x] Handoff DB: run the importer locally on the real workbooks to produce the private user's encrypted profile, with every unresolved mismatch listed for review.
- [x] Imports page: pick workbooks, read the dry run, import, and work through the review queue.

**Done when:** synthetic imports pass, a failed import leaves the profile untouched, and the handoff DB opens in Mossmint with its review queue intact.

## M4: Desktop dashboard and daily flows (v0.4)

- [x] Overview, Cash Flow, Paychecks, Debts, Bills, Savings, Transactions, following the mockup. (What waits is listed in the M4 decisions.)
- [x] Manual ledger entry, mark paid with undo, balance reconciliation.
- [ ] Every chart has a table twin; keyboard-only paths; 125–200% scaling; dark, light and high contrast.
- [x] Automated accessibility checks in CI: every focusable control has an accessible name, and the theme's colour pairs meet contrast ratios. A Narrator pass by hand each milestone from here on.
- [ ] Generic onboarding for a user with no spreadsheets.

**Done when:** a new user can onboard with no spreadsheet, an imported user can trace every record to its source, and no screen needs the network.

## M5: Scenarios and debt planning (v0.5)

- [ ] Payoff comparison: avalanche, snowball, custom.
- [ ] Largest feasible extra payment over the whole horizon, revalidated against daily cash.
- [ ] Stress scenarios and a debt-free date range.
- [ ] Apply to plan with a change summary; scenarios never change the real plan silently.

**Done when:** an extra payment is never shown as safe if the forecast under the chosen assumption drops below the cushion.

## M6: Windows release (v0.6)

- [x] Velopack installer, release CI on tags, checksums (moved up for the handoff; see the releases decisions).
- [x] Opt-in update check in Settings: **Check now**. It asks GitHub Releases only, through an explicit `GithubSource`, never Velopack's hosted service. Download and restart are separate steps, never during a backup or an import. Offline use never depends on it. ADR-0003 lists what GitHub sees. (Moved up ahead of M4; see the 2026-10-10 decisions.)
- [ ] An optional daily update check, off by default.
- [ ] JSON and CSV export, with an explicit warning. Exported CSV is escaped so a spreadsheet can't run it as formulas, and the importer undoes the escaping.
- [ ] Migration safety: a migration interrupted by a crash is recovered on the next open, from its snapshot.
- [ ] Code signing through Azure Artifact Signing.
- [ ] Upgrade test: install, create a profile, back up, update, restore.
- [ ] User docs and a sample-data generator.

**Done when:** clean install → new or imported profile → backup → update → restore passes, and the public repo holds no private financial content.

## M7: Bank data in (v0.7)

- [ ] CSV with column mapping, through a hand-written parser:
  - UTF-8 first, then Windows-1252 with a preview;
  - delimiters and date formats detected;
  - sign conventions and split debit/credit columns handled;
  - mappings saved by the header row's fingerprint;
  - presets for exports from YNAB, Monarch, Actual and Mint, so history comes along.
- [ ] OFX/QFX statement files through a hand-written, hardened parser (SGML 1.x and XML 2.x). Duplicates are caught by the bank's transaction ID.
- [ ] SimpleFIN adapter:
  - The user claims a setup token once. The credentials live only in the encrypted profile, never in a URL.
  - Syncing is manual, with **Sync now**. Request windows overlap, and errors are shown per account.
- [ ] Bank-reported balances become connector balance snapshots, so a fresh balance can clear *needs confirmation*.
- [ ] One transaction source per account until 0.8 detects duplicates. A deleted imported transaction stays deleted.
- [ ] A forecast journal: each forecast is kept as it was made, for 0.9's backtests.

**Done when:** every offline workflow still works with any connector unconfigured, stale or offline.

## M8: Matching real activity to the plan (v0.8)

- [ ] Suggested matches: a transaction that looks like a bill payment or a paycheck is offered as a match. The user confirms it, and the bill is marked paid. Nothing is matched silently. When two candidates are close, Mossmint suggests neither and raises a review item. The matching rules are written down in `docs/financial-models/`.
- [ ] Manual match, merge and unmatch. A transfer between the user's own accounts pairs both sides, so it never counts as spending.
- [ ] Pending transactions:
  - Each is linked to its posted version, whose amount may differ (a tip, for example).
  - One still unposted after 14 days goes to review.
  - Pending debits count against safe to spend.
- [ ] Balance reconciliation against the bank's reported balance; drift becomes a review item.
- [ ] Duplicate detection when sources overlap, such as a CSV and SimpleFIN covering the same days. Duplicates are linked, never deleted.
- [ ] Connection health: the last sync, errors and stale data for each connected account.
- [ ] Categories and simple payee rules, applied as suggestions that keep their source. Only the categories 0.9 needs; rules can be turned off.
- [ ] Low-point alerts as local Windows notifications, with no amounts on the lock screen.

**Done when:** a month of synthetic feed data reconciles to the plan with every match confirmed or explained, and no transaction counts twice.

## M9: Learning from history (v0.9)

- [ ] Spending ranges per pay period feed the scenarios: the median for base, the upper quartile for cautious, the maximum for stress. Each is labelled with how many periods it came from and how often such a range holds.
- [ ] Seasonal estimates, only with 13 or more months of history; otherwise *not enough history*.
- [ ] Forecast backtesting from the journal: error by days ahead, bias, and error in the lowest point before payday, as a table and a chart. A reconstructed past forecast is labelled as one.

**Done when:** a scenario says which history it learned from, and backtests on synthetic history give known answers.

## 1.0: The promise release

- [ ] Compatibility promise: every later version opens 1.0 profiles and backups and imports the same workbook shapes, checked against recorded fixtures pinned by checksum. The fixtures are synthetic, generated by each release's own code, and need a narrow exception to the no-`.mmbak`/`.db` rule. Decide it before 0.6, so 0.3.0's fixture can be captured.
- [ ] Migration tests from every released schema.
- [ ] Accessibility audit that confirms the automated checks: Narrator and NVDA scripts, keyboard-only use, 200% scaling and high contrast.
- [ ] Threat model refresh and complete user docs.
- [ ] Betas on the `win-beta` channel before the release.
- [ ] Protect `main` with a ruleset like Prompuff's; every change lands through a pull request.
- [ ] Launch video made with `/brag`, from synthetic data only: safe to spend before payday, the lowest point before payday, a review item that won't guess, and everything staying on the PC. The working files stay in the gitignored `brag-output/`.
- [ ] Landing page in `site/`, deployed to GitHub Pages: plain HTML and CSS, the app's own fonts, a Content-Security-Policy that blocks anything from other sites (a deploy check enforces it), downloads that link to the latest release, and the privacy contract up front.

**Done when:** a profile from any 0.x release upgrades cleanly to 1.0, the betas surface no data-loss bugs, the promise is written in the README, and the video and the landing page are live.

## Later

- macOS and Linux builds.
- A live Safe to spend preview while typing an entry, and *Override a date* for a single paycheck.
- Plaid relay (a stretch goal after 1.0), only if demand justifies it, behind its own ADR and threat model.

## Maybe later

- **A mobile companion, Android first, with no accounts.** It starts read-mostly, opening the same encrypted `.mmbak` backups. It could later sync through a cloud folder the user already has: the PC stays the source of truth, and phone entries (mark paid, a quick expense) go to an encrypted inbox that the desktop merges through the review queue. Direct phone-to-PC sync over Wi-Fi and an iOS app (which needs an Apple Developer account) depend on demand. A Mossmint-run sync server stays out: it would break the no-accounts promise.

## Not planned

- Angular, an ASP.NET server, or any mandatory cloud backend.
- Bank credential login forms, scraping bank portals, or automatic transfers and payments.
- LLM-driven financial decisions.
- Telemetry, analytics or crash upload.
- A third-party executable plugin system.
