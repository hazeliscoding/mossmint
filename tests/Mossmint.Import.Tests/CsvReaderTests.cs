using System.Text;
using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

public sealed class CsvReaderTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Reads_quoted_fields_with_delimiters_doubled_quotes_and_line_breaks()
    {
        var table = CsvReader.Read(Utf8("Date,Description,Amount\r\n2026-01-02,\"Corner Cafe, Main St\",-4.50\r\n2026-01-03,\"Said \"\"hi\"\"\nsecond line\",12\r\n2026-01-04,Plain,1"));

        table.Header.Should().Equal("Date", "Description", "Amount");
        table.Rows.Select(r => r.Fields[1]).Should().Equal("Corner Cafe, Main St", "Said \"hi\"\nsecond line", "Plain");
        table.Rows.Select(r => r.Line).Should().Equal(2, 3, 5);
        table.Encoding.Should().Be(CsvEncoding.Utf8);
        table.Delimiter.Should().Be(',');
    }

    [Fact]
    public void Strips_a_utf8_byte_order_mark_and_keeps_accents()
    {
        var table = CsvReader.Read([.. Encoding.UTF8.GetPreamble(), .. Utf8("Date,Description\n2026-01-02,Café Olé\n")]);

        table.Header[0].Should().Be("Date");
        table.Rows[0].Fields[1].Should().Be("Café Olé");
        table.Encoding.Should().Be(CsvEncoding.Utf8);
    }

    [Fact]
    public void Falls_back_to_windows_1252_when_the_bytes_are_not_utf8_and_says_so()
    {
        // 0xE9 is é and 0x80 is € in Windows-1252; neither is valid UTF-8 on its own.
        byte[] content = [.. Encoding.ASCII.GetBytes("Date,Description\n2026-01-02,Caf"), 0xE9, .. Encoding.ASCII.GetBytes(" "), 0x80, 0x0A];

        var table = CsvReader.Read(content);

        table.Rows[0].Fields[1].Should().Be("Café €");
        table.Encoding.Should().Be(CsvEncoding.Windows1252);
    }

    [Fact]
    public void Reads_utf16_with_a_byte_order_mark()
    {
        var table = CsvReader.Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Date\tDescription\tAmount\r\n2026-01-02\tCafé\t-4.50\r\n")]);

        table.Delimiter.Should().Be('\t');
        table.Rows[0].Fields.Should().Equal("2026-01-02", "Café", "-4.50");
        table.Encoding.Should().Be(CsvEncoding.Utf16);
    }

    [Theory]
    [InlineData(',', "Date,Description,Amount\n2026-01-02,Cafe,-4.50\n2026-01-03,Shop,-1.00\n")]
    [InlineData(';', "Date;Description;Amount\n02.01.2026;Cafe;-4,50\n03.01.2026;Shop;-1,00\n")]
    [InlineData('\t', "Date\tDescription\tAmount\n2026-01-02\tCafe, deli\t-4.50\n2026-01-03\tShop\t-1.00\n")]
    [InlineData('|', "Date|Description|Amount\n2026-01-02|Cafe; deli|-4.50\n2026-01-03|Shop|-1.00\n")]
    [InlineData(',', "Date,Description,Amount\n2026-01-02,ref;a;b;c,-4.50\n2026-01-03,ref;d;e;f,-1.00\n")]
    public void Detects_the_delimiter(char delimiter, string text)
    {
        var table = CsvReader.Read(Utf8(text));

        table.Delimiter.Should().Be(delimiter);
        table.Header.Should().Equal("Date", "Description", "Amount");
        table.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Skips_lines_before_the_header()
    {
        var table = CsvReader.Read(Utf8("Account Name : Everyday Checking\nAccount Number : XXXX1234\n\nDate,Description,Amount,Balance\n2026-01-02,Cafe,-4.50,95.50\n2026-01-03,Shop,-1.00,94.50\n"));

        table.Header.Should().Equal("Date", "Description", "Amount", "Balance");
        table.Rows.Should().HaveCount(2);
        table.SkippedLines.Should().Be(2);
    }

    [Fact]
    public void A_file_without_a_header_gets_numbered_columns()
    {
        var table = CsvReader.Read(Utf8("\"01/02/2026\",\"-4.50\",\"*\",\"\",\"CORNER CAFE\"\n\"01/03/2026\",\"-1.00\",\"*\",\"\",\"SHOP\"\n"));

        table.HasHeader.Should().BeFalse();
        table.Header.Should().Equal("Column 1", "Column 2", "Column 3", "Column 4", "Column 5");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Fields[4].Should().Be("CORNER CAFE");
    }

    [Fact]
    public void Handles_every_line_ending_and_skips_blank_lines()
    {
        var table = CsvReader.Read(Utf8("Date,Amount\r2026-01-02,1\n\n2026-01-03,2\r\n   \r\n2026-01-04,3"));

        table.Rows.Select(r => r.Fields[1]).Should().Equal("1", "2", "3");
        table.Rows.Select(r => r.Line).Should().Equal(2, 4, 6);
    }

    [Fact]
    public void An_unterminated_quote_names_its_line()
    {
        var read = () => CsvReader.Read(Utf8("Date,Description\n2026-01-02,ok\n2026-01-03,\"never closed\n2026-01-04,more\n"));

        read.Should().Throw<CsvFormatException>().WithMessage("*line 3*");
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var read = () => CsvReader.Read(Utf8("\r\n\r\n"));

        read.Should().Throw<CsvFormatException>();
    }
}
