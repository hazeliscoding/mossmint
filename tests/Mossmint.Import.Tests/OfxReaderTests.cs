using System.Text;
using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

// Statement files are built here by hand: an OFX 1.x SGML file with its
// unclosed leaf elements, and an OFX 2.x XML file with closed ones.
public sealed class OfxReaderTests
{
    internal const string Sgml = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20261012120000<LANGUAGE>ENG</SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <TRNUID>1
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <STMTRS>
        <CURDEF>USD
        <BANKACCTFROM>
        <BANKID>011000015
        <ACCTID>000123456789
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20261001
        <DTEND>20261012
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20261001120000.000[-5:EST]
        <TRNAMT>1840.00
        <FITID>202610010001
        <NAME>PAYROLL DEPOSIT
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20261002
        <TRNAMT>-4.75
        <FITID>202610020001
        <NAME>CORNER CAFE &amp; DELI
        <MEMO>CARD 1234
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>POS
        <DTPOSTED>20261003
        <TRNAMT>-12.00
        <FITID>202610030001
        <MEMO>BOOKSHOP
        </STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL>
        <BALAMT>2507.65
        <DTASOF>20261012120000
        </LEDGERBAL>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    internal const string Xml = """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <?OFX OFXHEADER="200" VERSION="220" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
        <OFX>
          <CREDITCARDMSGSRSV1>
            <CCSTMTTRNRS>
              <TRNUID>1</TRNUID>
              <CCSTMTRS>
                <CURDEF>USD</CURDEF>
                <CCACCTFROM><ACCTID>4111000000001234</ACCTID></CCACCTFROM>
                <BANKTRANLIST>
                  <DTSTART>20261001</DTSTART><DTEND>20261012</DTEND>
                  <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261004</DTPOSTED><TRNAMT>-25.10</TRNAMT><FITID>CC-1</FITID><NAME>Green Grocer</NAME></STMTTRN>
                  <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20261008</DTPOSTED><TRNAMT>100.00</TRNAMT><FITID>CC-2</FITID><NAME>Payment &#8211; thank you</NAME></STMTTRN>
                </BANKTRANLIST>
                <LEDGERBAL><BALAMT>-311.42</BALAMT><DTASOF>20261012</DTASOF></LEDGERBAL>
              </CCSTMTRS>
            </CCSTMTTRNRS>
          </CREDITCARDMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public void Reads_an_sgml_bank_statement_with_unclosed_elements()
    {
        var statement = OfxReader.Read(Encoding.ASCII.GetBytes(Sgml)).Statements.Should().ContainSingle().Subject;

        statement.Should().BeEquivalentTo(new { BankId = "011000015", AccountId = "000123456789", Type = OfxAccountType.Checking, Currency = "USD" });
        statement.Transactions.Select(t => (t.FitId, t.Posted, t.Amount, t.Description)).Should().Equal(
            ("202610010001", new DateOnly(2026, 10, 1), 1840.00m, "PAYROLL DEPOSIT"),
            ("202610020001", new DateOnly(2026, 10, 2), -4.75m, "CORNER CAFE & DELI"),
            ("202610030001", new DateOnly(2026, 10, 3), -12.00m, "BOOKSHOP"));
        statement.Ledger.Should().Be(new OfxBalance(2507.65m, new DateOnly(2026, 10, 12)));
        statement.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Reads_an_xml_credit_card_statement()
    {
        var statement = OfxReader.Read(Encoding.UTF8.GetBytes(Xml)).Statements.Should().ContainSingle().Subject;

        statement.Type.Should().Be(OfxAccountType.CreditCard);
        statement.AccountId.Should().Be("4111000000001234");
        statement.Transactions.Select(t => (t.FitId, t.Amount, t.Description)).Should().Equal(("CC-1", -25.10m, "Green Grocer"), ("CC-2", 100.00m, "Payment – thank you"));
        statement.Ledger.Should().Be(new OfxBalance(-311.42m, new DateOnly(2026, 10, 12)));
    }

    [Fact]
    public void Reads_every_statement_in_a_file()
    {
        var two = Sgml.Replace("</BANKMSGSRSV1>", """
            <STMTTRNRS><TRNUID>2<STMTRS><CURDEF>USD<BANKACCTFROM><BANKID>011000015<ACCTID>000987654321<ACCTTYPE>SAVINGS</BANKACCTFROM>
            <BANKTRANLIST><DTSTART>20261001<DTEND>20261012<STMTTRN><TRNTYPE>INT<DTPOSTED>20261010<TRNAMT>0.42<FITID>S-1<NAME>INTEREST</STMTTRN></BANKTRANLIST>
            <LEDGERBAL><BALAMT>500.42<DTASOF>20261012</LEDGERBAL></STMTRS></STMTTRNRS>
            </BANKMSGSRSV1>
            """, StringComparison.Ordinal);

        var statements = OfxReader.Read(Encoding.ASCII.GetBytes(two)).Statements;

        statements.Select(s => (s.AccountId, s.Type, s.Transactions.Count)).Should().Equal(("000123456789", OfxAccountType.Checking, 3), ("000987654321", OfxAccountType.Savings, 1));
    }

    [Fact]
    public void Reads_windows_1252_text_when_the_header_says_so()
    {
        var bytes = Encoding.ASCII.GetBytes(Sgml.Replace("CORNER CAFE &amp; DELI", "CAFE \u0001", StringComparison.Ordinal));
        bytes[Array.IndexOf(bytes, (byte)1)] = 0xE9;

        var statement = OfxReader.Read(bytes).Statements.Single();

        statement.Transactions[1].Description.Should().Be("CAFE é");
    }

    [Fact]
    public void Ignores_elements_it_does_not_know_such_as_quicken_extensions()
    {
        var qfx = Sgml.Replace("<SIGNONMSGSRSV1>", "<SIGNONMSGSRSV1><INTU.BID>3000<INTU.USERID>someone", StringComparison.Ordinal);

        OfxReader.Read(Encoding.ASCII.GetBytes(qfx)).Statements.Single().Transactions.Should().HaveCount(3);
    }

    [Fact]
    public void A_transaction_missing_its_id_date_or_amount_is_reported_not_guessed()
    {
        var broken = Sgml.Replace("<FITID>202610020001\n", "", StringComparison.Ordinal).Replace("<TRNAMT>-12.00", "<TRNAMT>twelve", StringComparison.Ordinal);

        var statement = OfxReader.Read(Encoding.ASCII.GetBytes(broken)).Statements.Single();

        statement.Transactions.Select(t => t.FitId).Should().Equal("202610010001");
        statement.Problems.Should().HaveCount(2).And.Contain(p => p.Contains("no FITID", StringComparison.Ordinal)).And.Contain(p => p.Contains("'twelve'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_element_doesnt_swallow_what_follows_it()
    {
        var empty = Sgml.Replace("<DTSTART>20261001", "<DTSTART>", StringComparison.Ordinal).Replace("<NAME>PAYROLL DEPOSIT", "<MEMO>\n<NAME>PAYROLL DEPOSIT", StringComparison.Ordinal);

        var statement = OfxReader.Read(Encoding.ASCII.GetBytes(empty)).Statements.Single();

        statement.Transactions.Should().HaveCount(3);
        statement.Transactions[0].Description.Should().Be("PAYROLL DEPOSIT");
        statement.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_character_reference_to_half_a_surrogate_pair_stays_as_written()
    {
        var odd = Xml.Replace("Payment &#8211; thank you", "Payment &#xD800; thank you", StringComparison.Ordinal);

        OfxReader.Read(Encoding.UTF8.GetBytes(odd)).Statements.Single().Transactions[1].Description.Should().Be("Payment &#xD800; thank you");
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE OFX [<!ENTITY x \"boom\">]><OFX>&x;</OFX>")]
    [InlineData("<?xml version=\"1.0\"?><!ENTITY x SYSTEM \"file:///c:/windows/win.ini\"><OFX></OFX>")]
    public void Refuses_document_type_and_entity_declarations(string text) =>
        FluentActions.Invoking(() => OfxReader.Read(Encoding.UTF8.GetBytes(text))).Should().Throw<OfxFormatException>();

    [Fact]
    public void Refuses_a_file_that_is_not_ofx() =>
        FluentActions.Invoking(() => OfxReader.Read(Encoding.UTF8.GetBytes("Date,Description,Amount\n"))).Should().Throw<OfxFormatException>();

    [Fact]
    public void Refuses_absurdly_deep_nesting()
    {
        var deep = "<OFX>" + string.Concat(Enumerable.Repeat("<STMTRS>", 500)) + "<CURDEF>USD" + string.Concat(Enumerable.Repeat("</STMTRS>", 500)) + "</OFX>";

        FluentActions.Invoking(() => OfxReader.Read(Encoding.UTF8.GetBytes(deep))).Should().Throw<OfxFormatException>().WithMessage("*nested*");
    }
}
