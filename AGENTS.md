# AGENTS.md

These are the working rules for agents in this repo. Mossmint is a local-first Windows desktop app (C#, .NET 10, Avalonia, MIT) for paycheck forecasting and debt payoff planning.

## Sources of truth

- `README.md`: the pitch and the privacy contract.
- `ROADMAP.md`: decisions already made, the milestones, and what is out of scope. It is also the status file. Check it before proposing features, and respect its decisions unless the owner reopens them.
- Work from the next unchecked item in `ROADMAP.md`. Don't build past the current milestone without asking. Tick items off and add dated decisions as you go.
- ADRs in `docs/architecture/` and `docs/connectors/` explain the larger decisions. Financial rules are written down in `docs/financial-models/`.

## Private financial data (hard rules)

- **Don't open, read, parse, copy, summarize or upload real financial files** (`.xlsx`, `.csv`, `.ofx`, profile databases, backups), even if they sit in the working tree, unless the owner has approved it for the task at hand. Reading one with an agent tool sends it off the machine. The current approvals are recorded in `ROADMAP.md` (2026-10-09: the workbooks may be read and used for the importer and the handoff profile).
- Even with approval, read only what the task needs (structure before values) and never modify the originals. Real workbooks are migrated only by Mossmint's own code, on the owner's machine, read-only.
- Fixtures, samples, tests and screenshots are synthetic and built in code. Never derive them from real files, not even anonymized. Values seen in a real file never go into the repo, commits, issues, Discord or chat summaries.
- Notes about the real workbooks go in `private/` (gitignored), never in tracked docs.
- Public files (code, docs, commits, issues, PRs) don't name the private user, their lenders or their workbook file names. Sheet names and column labels may appear in importer code; recognize workbooks by structure, never by file name.
- Never use `git add -f`. `.gitignore` blocks spreadsheets and databases on purpose.

## Privacy contract

- No network calls during normal use. Only an explicit, opt-in update check or future connector may use the network, and never from `Domain`, `Engine` or `Application`.
- No telemetry, analytics or crash upload.
- Logs never contain balances, transaction names, account numbers, tokens or file paths.

## Financial correctness

- Money is `decimal` through `Money`. Never `double` or `float` in a money path. Mixing currencies throws.
- Use `DateOnly` for financial dates and `DateTimeOffset` for audit instants. Never `DateTime.Now` inside `Domain` or `Engine`: the caller passes the date.
- Every figure carries its source and as-of date. Never present an estimate as confirmed.
- When inputs can't support an answer, return an explicit result (*never pays off*, *needs confirmation*) with reasons. Never a fake date or amount.
- Rounding happens at the documented step for each model, with an explicit `MidpointRounding`. Document every new rule in `docs/financial-models/`.
- Every bug in debt payoff or cash-flow logic gets a regression test. Prefer hand-derived oracle fixtures over matching spreadsheet output.

## Profiles and storage

- Only `Mossmint.Persistence` talks to SQLite, and only through `EncryptedSqlite.Open`. Never add `Microsoft.Data.Sqlite` (non-Core) or another SQLitePCLRaw bundle: a second SQLite binary could open profiles unencrypted.
- Every schema change is an EF Core migration; never edit the database by hand. A pre-migration snapshot is taken automatically.
- Tests that need a profile use a temporary root and cheap Argon2 settings; production keeps `Argon2Settings.Default`.

## Architecture

- `Desktop → Application → Domain + Engine`. Infrastructure projects implement Application ports. `Engine` references only `Domain`; `Domain` references nothing. `ArchitectureTests` enforces it.
- `Domain` and `Engine` are pure: no I/O, no UI, no EF, no clock, no randomness.
- No EF entity or spreadsheet cell becomes a domain type.
- Keep abstractions small and add them only with a real consumer.

## UI

- Follow `docs/design/`. Colours come from theme resources (`DynamicResource`), never literals in views.
- Numbers, dates and IDs use the mono font with tabular figures. Negative amounts use a true minus (`−`). Red is for shortfalls only, never for ordinary spending.
- Colour is never the only signal: every status pairs a shape with a text label.
- Every focusable element shows the focus ring. Every chart has a table equivalent.

## Commands

- `dotnet build` and `dotnet test` from the repo root.
- `dotnet run --project src/Mossmint.Desktop` runs the app.
- `dotnet format --verify-no-changes` is the format check CI runs.
- `dotnet run --project tools/Mossmint.Screenshots -- <out-dir>` renders synthetic screenshots.
- `pwsh build/pack.ps1 [-Version x.y.z]` publishes win-x64 and packs a Velopack installer into `artifacts/releases`.
- `Mossmint.Desktop.exe --verify-encryption [report.json]` proves a build encrypts profiles (exit code 0 = pass). Run it against packaged builds.
- `dotnet ef migrations add <Name> --project src/Mossmint.Persistence --startup-project src/Mossmint.Persistence --output-dir Data/Migrations --namespace Mossmint.Persistence.Data.Migrations` adds a schema migration.
- Set `MOSSMINT_DATA_ROOT` to a temporary folder whenever you run the app for testing; never point tools at `%LOCALAPPDATA%\Mossmint`.

## Working style

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `chore:`, `test:`, `ci:`, `build:`, `refactor:`). Keep each commit atomic and use a scope when it helps (`feat(engine): …`).
- **No AI attribution** in commits or PRs: no `Co-Authored-By` trailers, no "Generated with" lines, no session links.
- **Checks:** automate acceptance checks. Every check for an absence gets a positive control.
- **Docs:** short. Prefer editing `ROADMAP.md` over new planning documents.
- **Diagrams** are Mermaid (flowcharts, sequence, state, ER) in fenced `mermaid` blocks, never ASCII art.
- **Comments** explain why, not what: a constraint, a financial rule and its source, a workaround and its cause. No boilerplate XML docs, no commented-out code.
- Local Playwright output goes to `.playwright-mcp/`, which git ignores.
