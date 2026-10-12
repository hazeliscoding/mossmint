using System.Text;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

public sealed class OfxImportTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly Account Checking = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-00000000000a")), "Everyday Checking", AccountKind.Checking, Currency.Usd);
    private static readonly Account Card = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-00000000000b")), "Rewards Card", AccountKind.CreditCard, Currency.Usd);

    [Fact]
    public void A_bank_statement_brings_its_transactions_and_balance_keyed_by_the_banks_ids()
    {
        var (file, ofx) = Read(OfxReaderTests.Sgml);

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = Checking }, Today, Currency.Usd);

        batch.Transactions.Select(t => (t.ExternalId, t.Amount)).Should().Equal(
            ("ofx:202610010001", Money.Usd(1840m)), ("ofx:202610020001", Money.Usd(-4.75m)), ("ofx:202610030001", Money.Usd(-12m)));
        batch.Transactions.Should().OnlyContain(t => t.AccountKey == ImportKeys.Account(Checking.Id));
        batch.Balances.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Balance = Money.Usd(2507.65m), AsOf = Today });
        batch.Origin.Should().Be(ImportOrigin.BankFile);
        batch.Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_card_statement_is_turned_into_what_is_owed()
    {
        var (file, ofx) = Read(OfxReaderTests.Xml);

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = Card }, Today, Currency.Usd);

        batch.Transactions.Select(t => t.Amount).Should().Equal(new[] { Money.Usd(25.10m), Money.Usd(-100m) }, "a charge raises what's owed and a payment lowers it");
        batch.Balances.Single().Balance.Should().Be(Money.Usd(311.42m));
    }

    [Fact]
    public void A_card_showing_a_credit_is_queued_for_a_look()
    {
        var (file, ofx) = Read(OfxReaderTests.Xml.Replace("<BALAMT>-311.42", "<BALAMT>311.42", StringComparison.Ordinal));

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = Card }, Today, Currency.Usd);

        batch.Findings.Should().ContainSingle(f => f.Code == "ofx-card-credit" && f.Severity == FindingSeverity.NeedsReview);
    }

    [Fact]
    public void The_account_number_stays_out_of_keys_labels_and_sources()
    {
        var (file, ofx) = Read(OfxReaderTests.Sgml);
        var statement = ofx.Statements[0];

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(statement)] = Checking }, Today, Currency.Usd);

        OfxImport.AccountKey(statement).Should().NotContain("123456789");
        OfxImport.Describe(statement).Should().Be("Checking ending 6789");
        batch.Transactions.Select(t => t.Source.ToString()).Concat(batch.Balances.Select(b => b.Source.ToString()))
            .Should().OnlyContain(s => !s.Contains("123456789", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_transactions_and_a_different_currency_block()
    {
        var (file, ofx) = Read(OfxReaderTests.Sgml.Replace("<TRNAMT>-12.00", "<TRNAMT>twelve", StringComparison.Ordinal).Replace("<CURDEF>USD", "<CURDEF>EUR", StringComparison.Ordinal));

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = Checking }, Today, Currency.Usd);

        batch.Findings.Where(f => f.Severity == FindingSeverity.Blocking).Select(f => f.Code).Should().BeEquivalentTo(["ofx-unreadable", "ofx-currency"]);
    }

    [Fact]
    public void A_repeated_bank_id_keeps_both_transactions_apart()
    {
        var (file, ofx) = Read(OfxReaderTests.Sgml.Replace("<FITID>202610030001", "<FITID>202610020001", StringComparison.Ordinal));

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = Checking }, Today, Currency.Usd);

        batch.Transactions.Select(t => t.ExternalId).Should().OnlyHaveUniqueItems().And.Contain("ofx:202610020001#2");
        batch.Findings.Should().ContainSingle(f => f.Code == "ofx-repeated-id" && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void A_statement_with_no_destination_is_left_out_with_a_note()
    {
        var (file, ofx) = Read(OfxReaderTests.Sgml);

        var batch = OfxImport.Map(file, ofx, new Dictionary<string, Account>(), Today, Currency.Usd);

        batch.Transactions.Should().BeEmpty();
        batch.Findings.Should().ContainSingle(f => f.Code == "ofx-account-skipped").Which.Title.Should().Contain("ending 6789");
    }

    private static (SourceFile File, OfxFile Ofx) Read(string text)
    {
        var file = new SourceFile("statement.qfx", Encoding.UTF8.GetBytes(text));
        return (file, OfxReader.Read(file.Content));
    }
}
