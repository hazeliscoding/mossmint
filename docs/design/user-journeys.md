<!-- Adapted from the Mossmint Claude Design project. Names are generic and every number is synthetic. -->

# User journeys and acceptance

The mockup (`Mossmint.dc.html` in the Claude Design project) uses one synthetic profile across every screen: a checking balance confirmed yesterday, a separate savings balance with a reserve goal, net pay twice a month (the 15th and the last day), nine bills, four debts, a checking cushion and a daily spending allowance. Screenshots in this repo use the synthetic *Juniper* profile.

## 1. Importing existing workbooks
Welcome → *Import my finances* → step 1 lists a master budget workbook and four per-debt workbooks; two show warnings → step 2 explains the recognized layouts and cross-file references → step 3 previews record counts per group → step 4 reconciliation: a loan balance that differs from the lender, a stale card balance, ambiguous dates, a duplicate payment, a `#REF!` → the user resolves some and flags the rest as unverified → step 5 snapshot → step 6 atomic commit → step 7 provenance report → Overview shows the forecast with *Differs from lender* and *Statement needed* badges carried through.

## 2. Starting from zero
Welcome → *Start fresh* → checking balance, a twice-monthly paycheck, rent, one card → Overview shows Safe to spend, the debt-free forecast as a range, and empty Savings and Scenarios modules that prompt without blocking.

## 3. Surprise groceries
Quick add (`Ctrl+N`) → an outflow → the preview line shows the new Safe to spend → Record → toast with undo → *What changed* lists the entry → a suggested extra card payment stays only while the lowest projected checking balance (the day before payday) stays above the cushion. If it drops below, the suggestion is withdrawn and the paycheck card says why.

## 4. What-if extra payment
Debts → a loan → comparison shows base, +$25, +$50 and custom, each with payoff date, interest left and **minimum daily cash** → Scenarios → a one-time extra large enough to push the lowest cash under the cushion → the card turns critical with a label → *Apply* shows the warning and the changed future events → the user saves the scenario instead.

## 5. New PC
Install → Welcome → *Restore an encrypted backup* → passphrase → the profile is back, with no account and no network.

## 6. A future connection goes stale
Connections lists the account as *Stale · manual entry resumed* with the last successful update time; Overview labels the balance with its as-of date instead of "now".

## Interaction map
- Sidebar: every destination. Bell: attention drawer. Quick add: Transactions with the add form open. Reconcile balance: Transactions with the reconcile form.
- Overview: Safe to spend → explanation inspector; Next paycheck → Paychecks; Debt-free → Debts; each debt row → that debt; alert links → where to fix it.
- Cash Flow: range, stress slider and late-paycheck toggle re-run the forecast and table.
- Bills: Mark paid → row becomes Paid, toast with undo.
- Scenarios: sliders drive the Custom column; Apply to plan → confirmation with the change list and a risk banner.
- Import: step list with Back and Continue; resolve or flag each issue; *Import anyway as unverified*.
- Settings: tabs; Back up now; Delete → type-to-confirm; General → replay first run.

## Accessibility acceptance checklist
- [ ] Every status is a shape plus a text label, never colour alone (chips, chart markers, table rows).
- [ ] Text contrast ≥ 4.5:1 in both themes; chart labels ≥ 11px; no clipped money at 100/125/150/200% scaling (test `−$12,345,678.90`).
- [ ] Full keyboard path: sidebar, page actions, tables (arrow keys), chart (focusable points, `F6` to table), inspector, modals (focus trap, `Esc`), toasts (undo reachable).
- [ ] Visible two-ring focus on every focusable element.
- [ ] Screen readers read metric cards as label, value, headline and as-of date; badges read "Estimated, needs confirmation".
- [ ] Reduce motion removes all transitions; nothing flashes faster than 3 Hz.
- [ ] High contrast maps every token to system colours; borders stay visible.
- [ ] Long names ellipsize with a tooltip; missing values show "—" with a reason, never 0.
- [ ] Number entry accepts locale decimal separators, paste and selection; the invalid state has a text message.
- [ ] No public artifact contains real spreadsheet data; the profile switcher shows names only until unlocked.
