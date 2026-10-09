# Contributing

Thanks for helping. Mossmint handles people's money, so correctness and privacy come before features. Please read this page and [AGENTS.md](AGENTS.md) (the same rules, written for coding agents) before opening a PR.

## Setup

- Windows 10 22H2 or later, x64.
- .NET 10 SDK (the exact feature band is pinned in `global.json`).
- Any editor; Rider and Visual Studio both open `Mossmint.slnx`.

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Mossmint.Desktop
```

Before pushing, run what CI runs:

```powershell
dotnet format --verify-no-changes
dotnet build --configuration Release
dotnet test --configuration Release
```

Screenshots for docs and PRs come from the headless renderer, which only ever shows synthetic data:

```powershell
dotnet run --project tools/Mossmint.Screenshots -- artifacts/screenshots
```

## Where things live

| Project | Purpose |
|---|---|
| `Mossmint.Domain` | Money, dates, accounts, debts, schedules. Pure C#, no dependencies. |
| `Mossmint.Engine` | Forecasts, interest accrual, payoff math. References only Domain. |
| `Mossmint.Application` | Use cases and ports (interfaces) for storage and imports. |
| `Mossmint.Persistence`, `Mossmint.Import.*` | Infrastructure that implements Application ports. |
| `Mossmint.Desktop` | Avalonia UI and the composition root. |

`tests/Mossmint.ArchitectureTests` fails the build if a reference breaks this layering.

## Rules that block a merge

- **No real financial data.** Not in code, fixtures, screenshots, issues or PRs. Build test data in code. If you need to describe a bug in your own finances, describe its shape with made-up numbers.
- **No network code** outside a future, explicitly approved connector or updater project.
- **Money is `decimal`** through `Money`, never `double`. Dates are `DateOnly`; the engine never reads the clock.
- **Every bug in payoff or cash-flow logic gets a regression test**, ideally with a hand-worked expected value (a short schedule you can check on paper) rather than output copied from the code under test.
- **New financial rules are documented** in `docs/financial-models/` with their rounding and ordering.
- **Never present an estimate as confirmed.** Results the inputs can't support come back as *needs confirmation* or *never pays off*, with reasons.

## Commits and PRs

- [Conventional Commits](https://www.conventionalcommits.org/): `feat(engine): …`, `fix(desktop): …`, `docs: …`, `test: …`.
- Keep commits atomic and PRs focused on one change.
- Larger decisions get a short ADR in `docs/architecture/`, and a dated line in `ROADMAP.md`.
- UI changes: include before and after screenshots from the headless renderer, in both themes.

## Reporting security issues

See [SECURITY.md](SECURITY.md). Please report privately, not in a public issue.
