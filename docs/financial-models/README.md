# Financial models

How Mossmint's engine turns inputs into numbers. Every rule here has tests in `tests/Mossmint.Engine.Tests` or `tests/Mossmint.Domain.Tests`, most with a value worked out by hand in the test's comment. When a rule changes, change this page and its tests together.

- [Loans](loans.md): daily simple interest and amortizing loans.
- [Credit cards](cards.md): the statement-cycle approximation.
- [Cash flow](cash-flow.md): the daily forecast, paycheck funding, safe to spend and scenarios.
- [Paying off several debts](payoff.md): avalanche, snowball, custom orders and rollover.

## Conventions that apply everywhere

### Money

- Amounts are `decimal` inside `Money`, which carries an ISO 4217 currency. Adding or comparing two currencies throws; there is no conversion.
- Precision is kept until a model's rounding step. Rounding is always explicit, to the currency's minor units, with a stated `MidpointRounding`. Lender-facing rounding defaults to **half away from zero**, the same as Excel's `ROUND`.
- Settled amounts are stored as whole minor units (`ToMinorUnits` refuses anything that still needs rounding).

### Dates

- Financial dates are `DateOnly`. The engine never reads the clock; the caller passes "today".
- A balance is **as of the start of its date**. Interest accrues for every day from that date up to, not including, the next event, and a payment posts at the start of its day. Days between two events are `later.DayNumber - earlier.DayNumber`.
- Schedules produce a *nominal* date, then adjust it to a business day if asked. A bill's due date is never moved by funding or forecasting.

### Business days

- `WeekendsOnly` treats Saturday and Sunday as closed.
- `UsFederalReserve` adds the Fed holiday schedule, which governs ACH direct deposits. A holiday on Sunday is observed Monday; one on Saturday is not observed (the Fed opens Friday). This is an assumption about the user's bank, and users can add their own closed days.

### Signs

- Balance snapshots are reported the way the institution reports them: a liability's balance is the amount owed, as a positive number.
- Transactions are signed as the account reports them: deposits and card charges positive, withdrawals and card payments negative.
- A transfer's legs (including a card or loan payment) must cancel out in net-worth terms, so a payment is never counted as spending.

### Provenance and confidence

Every figure carries a source, an as-of date and a confidence (`Confirmed`, `Scheduled`, `Estimated`, `NeedsReview`). A derived figure takes the weakest confidence among its inputs. Projections are never more than `Scheduled`, card projections never more than `Estimated`. See [ADR-0002](../architecture/ADR-0002-forecasting-semantics.md).

### Answers the inputs can't support

- Payoff results are `PaysOff`, `NeverPaysOff` or `InsufficientData`. There is no fallback date.
- Safe to spend is a `SafeAmount`, a `Shortfall` or `NeedsConfirmation` with what to confirm.

### Imported transactions

- A bank file says how it shows money leaving the account (see [bank files](../operations/bank-files.md#amounts)). Mossmint turns that into money in or out, then signs it the way the account reports it: on checking, savings and cash, spending is negative; on a card or loan, a charge is positive and a payment negative. Mossmint's own export is already signed this way and is taken as is.
- An amount with fractions of a cent is refused, never rounded.
- A CSV row has no ID of its own, so Mossmint makes one from its date, amount, description (with runs of spaces collapsed) and how many identical rows came before it in the file. Two identical coffees on one day stay two transactions, and a later file that overlaps the same days gives them the same IDs, so they aren't imported twice. An edited description or amount makes a new row.
- Imported transactions are *needs review* (`Imported`), like every imported figure.

### Replay

Engine inputs and outputs are plain records that serialize to JSON. Replaying a serialized input gives a byte-identical serialized output (`ReplayTests`).
