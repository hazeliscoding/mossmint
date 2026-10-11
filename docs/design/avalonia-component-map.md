<!-- Imported from the Mossmint Claude Design project. Written for Avalonia 11; the app targets Avalonia 12.1 (see ROADMAP decisions). -->

# Mossmint → Avalonia component map

Target: Avalonia 11, MVVM (CommunityToolkit.Mvvm or ReactiveUI), Fluent theme base with Mossmint resource dictionaries. No WebView anywhere.

## Theme resources

- `Themes/Mossmint.Dark.axaml`, `Themes/Mossmint.Light.axaml`: one `ResourceDictionary` each, keys named after tokens (`BgCanvas`, `BgSurface`, `TextPrimary`, `Accent`, `SignalWarning`…). Switch with `Application.RequestedThemeVariant`; follow system by default.
- `Themes/Density.axaml`: `RowHeightCompact=32`, `RowHeightDense=28`; apply via a `Density` attached property that sets `MinHeight` on rows.
- Fonts in `Assets/Fonts`, exposed as `FontSans`, `FontMono`; `TextBlock` style class `.money` sets `FontFamily=FontMono`, `FontFeatures="tnum"` (Avalonia 11.1+).

## Shell

| Screen element | Control | Notes |
|---|---|---|
| Window chrome | `Window` with `ExtendClientAreaToDecorationsHint=True`, `ExtendClientAreaChromeHints=NoChrome`, custom caption buttons | Drag region = title bar `Border` with `IsHitTestVisible` and `BeginMoveDrag`; respect `SystemDecorations` on Linux/mac later |
| Sidebar | `ListBox` of `NavItem` records, `ItemTemplate` with icon+label, collapsed state via `Classes="collapsed"` | Badge `TextBlock` bound to `AttentionCount` |
| Top bar | `Grid` in a `UserControl TopBarView` | Profile switcher `DropDownButton`; search `AutoCompleteBox` wired to `Ctrl+K` `KeyBinding` |
| Inspector | `SplitView` (`DisplayMode=Inline` ≥1100px, `Overlay` below) | `IsPaneOpen` bound to `InspectorVm != null` |
| Attention drawer | same `SplitView`, different pane content via `ContentControl` + `DataTemplates` | |
| Toast with undo | `WindowNotificationManager` or a custom `ToastHost` `ItemsControl` | Undo = `ICommand` on toast VM, 10s timer |
| Modals | `Window` with `ShowDialog` or an in-window `Overlay` `Panel` | Type-to-confirm `TextBox` enables the danger button via binding |

## Overview

| Element | Control |
|---|---|
| Metric cards | `MetricCardView : UserControl` (Label, Value, Human, Details `IEnumerable<string>`, Tone, Explain, Command). `ItemsControl` with `UniformGrid Columns=4` |
| Forecast chart | Custom `Control` overriding `Render` (SkiaSharp via `DrawingContext`) or LiveCharts2 `CartesianChart`. Must expose an `AutomationProperties.Name` and a `DataGrid` twin (Cash Flow table) |
| Upcoming bills / What changed / Alerts | `ItemsControl` with `StackPanel`; rows as `DataTemplate` |
| Debt mini cards | `ItemsControl`, item is `Button` with `Command=OpenDebt` and `CommandParameter=DebtId` |
| Savings rings | `Arc`-based `GoalRing` custom control (`StrokeDashArray` on an `Ellipse` is enough) |

## Cash Flow

- Chart as above; `DataGrid` (`Avalonia.Controls.DataGrid`) for the daily table with `IsReadOnly`, row class `below-cushion` set by a `DataGridRow` style selector bound to `IsBelowCushion`.
- Range `SegmentedControl` = `ListBox` with horizontal `ItemsPanel` and radio-style template; stress test `Slider` + `CheckBox` bound to a `StressTestVm` that re-runs the forecast engine on change (debounced 120ms).

## Paychecks, Debts, Bills, Transactions, Savings

- Paycheck cards: `ItemsControl` of `PaycheckVm`; lines are an inner `ItemsControl`. Negative leftover binds to a `warning` class.
- Debt cards: `ListBox` (single selection) so arrow keys move selection; detail panel `ContentControl` bound to `SelectedDebt`.
- Comparison columns: `UniformGrid Columns=4`; each column a `DataTemplate` over `PayoffOption` records.
- Bills: `DataGrid` with a template column for `Mark paid` (`Button Command=MarkPaidCommand`). Use `DataGrid` only where sorting/resizing matters; otherwise `ItemsControl` + `Grid` with `SharedSizeGroup` is lighter and easier to style.
- Transactions: `DataGrid` with virtualization; duplicate rows flagged via `Classes`. Add/Reconcile forms are `Expander`-like inline panels (`IsVisible` bound to `IsAddOpen`), not dialogs, so the ledger stays visible.
- Currency input: custom `MoneyBox : TextBox` with `CultureInfo`-aware parsing, `NumberStyles.Currency`, selection-on-focus, Up/Down stepping, paste sanitizing, and an `Invalid` pseudo-class with a message `TextBlock` for screen readers (`AutomationProperties.HelpText`).

## Scenarios

- Left inputs: `Slider` controls bound to `ScenarioInputs`; engine recompute on a background `Task` with cancellation; results update `CustomScenario`.
- Cards: `ListBox` (selection) with four `ScenarioVm`; `Apply` opens `ApplyPlanDialog` showing `IReadOnlyList<PlannedChange>` (what / from / to) and a warning when `LowestCash < Cushion`.

## Import wizard

- `ImportWizardView` hosts a `TransitioningContentControl` keyed by `Step`; step list is a `ListBox` on the left.
- Reconciliation issues: `ItemsControl` of `IssueVm` with `ResolveCommand`, `FlagUnverifiedCommand`; both mutate the import staging model only; nothing persists until step 6 (`ImportSession.CommitAsync()` in one transaction after `BackupService.SnapshotAsync()`).
- Provenance table: `DataGrid`, exportable to CSV.

## Connections, Settings, Backups

- Connections: static copy + `ItemsControl` of providers with `IsEnabled=False` until a provider is implemented; no network code referenced from the view.
- Settings: `TabControl` (Backups / General / About). General rows = `ItemsControl` of `SettingRow`.
- Backups: `BackupService` exposes `LastBackupAge`, `VerifyRestoreAsync`; the age banner is a `Banner` `UserControl` with `Tone`.

## Accessibility hooks

- Every status badge sets `AutomationProperties.Name="{Label}, {Status}"`.
- Charts: `AutomationProperties.Name` describing the lowest point and cushion; `F6` cycles to the data table.
- Reduce motion: read `PlatformSettings.GetColorValues()` / `IPlatformSettings` motion preference, set a global `TransitionsEnabled` resource.
- Scaling: no fixed pixel widths on text containers; test `RenderScaling` 1.0–2.0; the main window's minimum is 760×480 (200% on a 1080p screen, or half of one at 125%). Tables scroll sideways rather than hide columns, card grids drop columns through container queries, and the sidebar collapses below 1200 px.

## Questions for the calculation engine (not to be faked in UI)

1. Exact definition of "sufficiently established" that unlocks Safe to spend vs *Needs confirmation* (max age of confirmed balance? all bills before payday have amounts?).
2. How estimated bill amounts are derived (N-month average? median? last?) and what range accompanies them.
3. Payday adjustment rules: previous vs next business day; which holiday calendar.
4. Daily allowance model: flat per-day vs per-paycheck pool, and how recorded spending draws it down.
5. Interest accrual method per debt type (daily simple for loans, average daily balance for cards) and when "accrued since statement" resets.
6. How the debt-free *range* is computed (which inputs are varied) so the UI can show "Jan–Jun 2029" honestly.
7. Transfer detection and linking rules so "all liquid accounts" never double counts.
8. Duplicate detection tolerance (date ± days, amount equality, description similarity).
9. What makes a lender/workbook balance conflict resolvable automatically vs always surfaced.
10. Apply-to-plan undo semantics: full snapshot vs reverse diff, and retention.
