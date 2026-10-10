# Mossmint

**Make room to grow.**

Mossmint is a local-first desktop app for paycheck forecasting and debt payoff planning. It tells you what is safe to spend before the next paycheck, which bills each paycheck has to cover, and what an extra debt payment really does to your cash. It works with the network off, it has no account to create, and nothing leaves your computer.

> **Status:** early development; [v0.3.0 is out](https://github.com/hazeliscoding/mossmint/releases/latest) for Windows x64. The financial engine (loans, cards, daily cash flow, safe to spend), encrypted profiles with verified backups, and read-only workbook import with a review queue are in place. The dashboard comes next (Milestone 4). See [ROADMAP.md](ROADMAP.md).

![Mossmint welcome screen in the dark theme](docs/screenshots/welcome-dark.png)

![Engine preview: safe to spend, the lowest point before payday, paycheck funding and debt projections for a synthetic profile](docs/screenshots/engine-preview-dark.png)

![Workbook import: the dry run lists every file with its SHA-256, what would change, and what needs review](docs/screenshots/import-preview-dark.png)

<sub>Screenshots are rendered headlessly from synthetic data. The engine preview shows real engine output for the synthetic *Juniper* profile; the dashboard itself arrives in Milestone 4.</sub>

## What makes it different

- **It never pretends to know.** Every balance, payment, APR and forecast carries its source and as-of date. A stale balance turns *Safe to spend* into *Needs confirmation*, with the reason, instead of a confident guess.
- **It watches the whole month, not just payday.** The forecast tracks the lowest point between paychecks, so a bill due the day before payday can't hide behind a healthy month-end total.
- **Debt math you can audit.** Daily-interest loans, amortizing loans and credit cards are modelled explicitly, with day-count, rounding and payment-allocation rules written down. If a payment can't overcome interest, you get "never pays off", not a fake date.
- **Your spreadsheets stay yours.** Importing Excel workbooks is read-only and previewed first. Originals are never modified.
- **Offline by default.** No telemetry, no analytics, and Mossmint never asks for your bank login. Update checks are opt-in.

## Privacy contract

- All data stays on this computer, in an encrypted profile database under `%LOCALAPPDATA%\Mossmint`.
- No network calls during normal use. Update checks and future bank connectors are separate, explicit opt-ins. A bank feed such as SimpleFIN is a service you sign up for separately, and it, not Mossmint, holds your bank login.
- No telemetry, crash upload or tracking.
- Logs never contain balances, transaction names, account numbers or file paths.

Details: [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md). How the numbers are computed: [docs/financial-models](docs/financial-models/README.md). How profiles and backups work: [docs/operations](docs/operations/backup-and-restore.md). How workbook import works: [docs/operations/workbook-import.md](docs/operations/workbook-import.md).

## Install

Download `Mossmint-Setup.exe` from the [latest release](https://github.com/hazeliscoding/mossmint/releases/latest). It installs for your Windows account only and needs no administrator rights. `SHA256SUMS` lists every file's checksum. Builds aren't code-signed yet, so Windows SmartScreen may warn on first launch: choose **More info**, then **Run anyway**.

Uninstalling removes the app but never your profiles, which live in `%LOCALAPPDATA%\Mossmint`.

## Build

Requires the .NET 10 SDK on Windows 10 22H2 or later (x64).

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Mossmint.Desktop
```

Contributor setup and checks: [CONTRIBUTING.md](CONTRIBUTING.md).

## Releases

1. Set the version in `Directory.Build.props` as `chore(release): x.y.z`, push it to `main`, and wait for CI.
2. Tag that commit and push the tag: `git tag v0.3.0 && git push origin v0.3.0`. Release tags can't be moved or deleted. A tag with a pre-release suffix, such as `v0.4.0-beta.1`, makes a beta on the `win-beta` channel.
3. The [Release workflow](.github/workflows/release.yml) tests and packs Mossmint, launches the portable build, installs with `Setup.exe`, runs the encryption self-check on both, and opens a **draft** GitHub Release with the installer, the portable zip, the Velopack update files and `SHA256SUMS`.
4. Read the draft, write its notes, and publish it.

Running the workflow by hand from the Actions tab builds the same files without publishing anything. `pwsh build/pack.ps1` packs locally.

## License

[MIT](LICENSE). Bundled fonts (Manrope, IBM Plex Mono) are under the SIL Open Font License.
