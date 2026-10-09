# ADR-0002: Forecasting semantics

- **Status:** Accepted
- **Date:** 2026-10-09

## Context

A forecast is only useful if people can tell how much to trust it. Spreadsheets mix confirmed balances, lender statements, guesses and stale cached formulas in one grid, and the result looks equally certain everywhere. Mossmint's engine has to keep those apart, give the same answer every time for the same inputs, and refuse to produce a number its inputs can't support.

## Decision

### Every figure carries provenance

Each balance, amount, rate and date has a **source**, an **as-of date** and a **quality**:

| Source | Meaning |
|---|---|
| `UserConfirmed` | The user checked it against their bank or lender |
| `LenderStatement` | Taken from a statement, with the statement date |
| `Imported` | Read from a workbook or file; carries its provenance |
| `Connector` | Observed by a future bank connector, with the provider's timestamp |
| `Scheduled` | A known future event (a bill due date, a paycheck rule) |
| `Estimated` | A user assumption or an average, with a range where one exists |
| `Derived` | Computed by the engine from the above |

A derived value is never more certain than its weakest input. The UI shows the label beside the value.

### Balances are observations, not transactions

A `BalanceSnapshot` records what an account held on a date. It is never booked as a transaction, and a new snapshot never rewrites a confirmed transaction. A difference between a snapshot and the ledger becomes a reconciliation item.

### The engine is a pure, replayable function

- Inputs: an opening position, schedules, obligations, assumptions and a horizon, all as plain records. The engine never reads the clock, files or network; the caller passes "today".
- Outputs: day-by-day results plus the reasons for each number. Inputs and outputs serialize to JSON, and replaying the same JSON gives an identical result.
- Money is `decimal`. Interest accrues at full precision and is rounded to cents only where the model says a lender would post it, with the rounding mode stated per model (default `MidpointRounding.AwayFromZero` for posted interest).

### Event order within a day is conservative

On one date, events apply in this order: opening position, outflows (bills, scheduled payments, transfers out, spending allowance), then inflows (paychecks, transfers in). This finds the lowest point a day can reach when a bill and a paycheck land on the same day and the deposit posts later. The order is a documented assumption that a later setting may relax; ties within a class sort by a stable key, never by insertion order.

### The cash-flow forecast watches the low point

The simulator reports opening, inflows, outflows, closing and **lowest cash** for every day, plus unfunded obligations. Month-end or payday totals never stand in for the lowest point. Negative cash is reported as a warning, never floored to zero.

### Answers the inputs can't support are explicit

- **Safe to spend** is computed only when there is a confirmed opening balance within the freshness window (default 3 days, configurable) and every obligation due before the next paycheck has an amount (fixed, or estimated with a range). Otherwise the result is `NeedsConfirmation`, listing what is missing.
- **Payoff** returns one of: `PaysOff` (date, totals of payments, interest and fees), `NeverPaysOff` (the payment doesn't cover accrual; includes the shortfall), or `InsufficientData` (what is missing). It never invents a date.
- A projected payoff is labelled a projection. It is not a lender's payoff quote.
- Card results are labelled approximate unless statement data covers the inputs the model needs.

### Scenarios are assumptions, not probabilities

Spending scenarios (`Base`, `Cautious`, `Stress`) come from ranges the user enters. They are labelled assumptions. The engine never presents confidence intervals or probabilities it can't justify from data.

### Plans and scenarios are separate

Running a scenario never changes the stored plan. Applying it is an explicit step with a change summary.

## Consequences

- Every engine result type has room for reasons and provenance, which costs some verbosity.
- Tests can compare JSON replays byte for byte, and hand-worked fixtures stay meaningful.
- The conservative same-day order can show a lower point than the bank will. That's deliberate, and the UI explains it.
