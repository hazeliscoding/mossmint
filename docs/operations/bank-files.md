# Bank files

Mossmint imports transactions from a CSV or OFX/QFX statement a bank lets you download, or from another app's export. A bank file goes through the same dry run, review queue and one-step commit as a [workbook](workbook-import.md). The original is only read.

```mermaid
flowchart LR
    file["CSV file"] -->|"read once, SHA-256"| reader["Built-in reader<br/>encoding, delimiter, header"]
    reader --> guess{"Saved mapping<br/>for this header?"}
    guess -->|yes| match["Match the columns<br/>(you check it)"]
    guess -->|no: guess or preset| match
    match --> batch["Import batch<br/>for the accounts you chose"]
    batch --> plan["Dry run"]
    plan -->|"you confirm"| commit["Snapshot, then one transaction;<br/>the mapping is remembered"]
```

## Reading

- **Encoding.** UTF-8 (with or without a byte order mark) and UTF-16 with a byte order mark. Bytes that aren't valid UTF-8 are read as Windows-1252, and the screen says so, because accented names may then look wrong.
- **Delimiter.** Comma, semicolon, tab or `|`: whichever splits the most rows into the same number of fields.
- **Header.** Lines above the header (account details some banks put first) are skipped and counted. A file whose first row already holds a date or an amount has no header, and its columns are numbered.
- **Quoting** follows RFC 4180, with any line ending. Nothing in a cell is ever evaluated.

## Matching the columns

The first time, Mossmint guesses the date, description and amount columns by their names, or by their values when the names don't say. Each choice shows an example from the file. You confirm or change it, and say which account the rows go to: one you have, or a new one. A file with an Account column can send each source account somewhere different, or leave one out.

Exports from **YNAB, Monarch, Actual, Mint and Mossmint** are recognized by their column names, so a reordered or extended header still matches.

When the import is saved, the mapping and the accounts you picked are stored in the profile, keyed by a fingerprint of the header row. The next file with the same header opens already matched.

### Amounts

| How the file writes them | Example |
|---|---|
| One column, spending is negative | `-4.50` |
| One column, spending is positive | `4.50` |
| Separate money out and money in columns | Outflow `4.50`, Inflow empty or `0.00` |
| Positive amounts and a debit/credit column | `4.50`, `debit` |
| As Mossmint exported them | Mossmint's own `transactions.csv` |

Currency symbols, thousands separators, `(4.50)` and `4.50-` are understood; decimal commas are detected. An amount with two signs (`(-4.50)`), more digits than any amount has, or a negative value in a column that already says which way the money went (money out, money in, or next to *debit*/*credit*) isn't read: it could be a reversal, so the row blocks with `csv-amount` rather than being guessed. How the amounts are signed in Mossmint is in [financial models](../financial-models/README.md#imported-transactions).

### Dates

Mossmint lists every format that reads all the dates in the column; a cell with no digits, like *Total*, doesn't count against a format. When `01/02/2026` could be January or February, the screen flags it and shows how the first date reads each way, so the choice is yours. A time after the date is ignored.

## What's left out, what blocks

| Code | Severity | When |
|---|---|---|
| `csv-row-skipped` | Note | A row has no date, like a totals line |
| `csv-date-format` | Blocking | Most rows don't have a date in the chosen format |
| `csv-amount` | Blocking | A dated row's amount can't be read, or its sign is ambiguous |
| `csv-fractional-amount` | Blocking | An amount has fractions of a cent |
| `csv-void-skipped` | Note | A Mossmint export's void entries |
| `csv-account-skipped` | Note | Rows for a source account you left out |
| `one-source` | Blocking | The account already gets transactions from another source |
| `typed-overlap` | For review | Entries you typed fall on days the file covers |

Until 0.8 can spot the same transaction arriving from two places, each account takes transactions from one source, and entries you typed by hand on the same days are queued so you can void any the file repeats. An imported entry you voided stays void when the file is imported again.

## Statement files (OFX and QFX)

A file whose first lines carry an OFX header or an `<OFX>` element is read as a statement, whatever its extension. QFX is OFX with Quicken's extra elements, which are ignored.

- **Reading.** A hand-written reader handles OFX 1.x SGML (leaf elements never close) and 2.x XML. It is not an XML parser: a document type or entity declaration refuses the file, only the five standard entities and character references are resolved, and nesting deeper than 32 levels is refused. A 1.x header naming code page 1252 is honored; otherwise the text is UTF-8, falling back to Windows-1252.
- **Accounts.** Each statement in the file (bank or card, several per file) names its account, so the only question is which Mossmint account it goes to. The screen shows the account's type and last four digits. The choice is remembered by a hash of the bank and account numbers; the account number itself is never stored or shown.
- **Transactions** are keyed by the bank's FITID, so a statement that overlaps an earlier one adds only what's new. If a bank repeats an ID within one file, both transactions are kept, with a note. A transaction missing its ID, date or amount blocks the import (`ofx-unreadable`).
- **Balances.** The statement's ledger balance becomes a balance snapshot on its date. It counts as bank-reported and confirmed, like a connection's, so a fresh one clears *needs confirmation*.
- **Signs.** OFX signs amounts from the account holder's side, cards included. On a card, Mossmint turns a negative balance into what's owed. A card statement showing a credit is queued for review (`ofx-card-credit`), because a few banks sign card balances the other way.
- A statement in another currency blocks the import (`ofx-currency`); one left without an account is left out with a note (`ofx-account-skipped`).

## Reading back Mossmint's export

Mossmint's `transactions.csv` matches the *Mossmint* preset. Its formula escaping (a leading `'` before text starting with `=`, `+`, `-`, `@`, a tab or a carriage return) is dropped, void entries are left out, and pending entries stay pending.
