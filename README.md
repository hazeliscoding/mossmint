# Mossmint

**Make room to grow.**

Mossmint is a local-first desktop app for paycheck forecasting and debt payoff planning. It tells you what is safe to spend before the next paycheck, which bills each paycheck has to cover, and what an extra debt payment really does to your cash. It works with the network off, it has no account to create, and nothing leaves your computer.

> **Status:** early development (Milestone 0). Nothing here is ready to hold real financial data yet. See [ROADMAP.md](ROADMAP.md).

## What makes it different

- **It never pretends to know.** Every balance, payment, APR and forecast carries its source and as-of date. A stale balance turns *Safe to spend* into *Needs confirmation*, with the reason, instead of a confident guess.
- **It watches the whole month, not just payday.** The forecast tracks the lowest point between paychecks, so a bill due the day before payday can't hide behind a healthy month-end total.
- **Debt math you can audit.** Daily-interest loans, amortizing loans and credit cards are modelled explicitly, with day-count, rounding and payment-allocation rules written down. If a payment can't overcome interest, you get "never pays off", not a fake date.
- **Your spreadsheets stay yours.** Importing Excel workbooks is read-only and previewed first. Originals are never modified.
- **Offline by default.** No telemetry, no analytics, no bank login. Update checks are opt-in.

## Privacy contract

- All data stays on this computer, in an encrypted profile database under `%LOCALAPPDATA%\Mossmint`.
- No network calls during normal use. Update checks and future bank connectors are separate, explicit opt-ins.
- No telemetry, crash upload or tracking.
- Logs never contain balances, transaction names, account numbers or file paths.

Details: [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md).

## Build

Requires the .NET 10 SDK on Windows 10 22H2 or later (x64).

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Mossmint.Desktop
```

Contributor setup and checks: [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE). Bundled fonts (Manrope, IBM Plex Mono) are under the SIL Open Font License.
