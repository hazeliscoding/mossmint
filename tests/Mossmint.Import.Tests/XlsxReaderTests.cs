using System.IO.Compression;
using System.Text;
using Mossmint.Import.Excel.Xlsx;
using Mossmint.Samples.Workbooks;

namespace Mossmint.Import.Tests;

public sealed class XlsxReaderTests
{
    [Fact]
    public void Reads_values_formulas_fills_and_hidden_sheets()
    {
        var writer = new XlsxWriter();
        writer.Sheet("Settings")
            .Set("A1", "APR").Set("B1", 0.2212m, null, "0.00%")
            .Set("A2", "As of").Set("B2", new DateOnly(2026, 8, 7))
            .Set("A3", "Paid").Set("B3", true)
            .Set("A4", "Daily").Set("B4", 0.000606m, "=B1/365")
            .Set("A5", "Label").Set("B5", "Text result", "=\"Text \"&\"result\"")
            .Set("D1", "Blue = Money Spent", fill: "FF00FFFF");
        writer.Sheet("Copy", hidden: true).Set("A1", 7m, "=IFERROR(__xludf.DUMMYFUNCTION(\"IMPORTRANGE(1)\"),7)");

        var book = XlsxReader.Read(writer.ToBytes());

        var settings = book.Sheet("settings")!;
        settings.Visibility.Should().Be(SheetVisibility.Visible);
        settings.Cell("B1")!.Number.Should().Be(0.2212m);
        settings.Cell("B1")!.IsDateFormat.Should().BeFalse();
        settings.Cell("B2")!.IsDateFormat.Should().BeTrue();
        book.ToDate(settings.Cell("B2")!.Number!.Value).Should().Be(new DateOnly(2026, 8, 7));
        settings.Cell("B3")!.Boolean.Should().BeTrue();
        settings.Cell("B4")!.Formula.Should().Be("=B1/365");
        settings.Cell("B4")!.Number.Should().Be(0.000606m);
        settings.Cell("B5")!.Text.Should().Be("Text result");
        settings.Cell("D1")!.Fill.Should().Be("FF00FFFF");

        var copy = book.Sheet("Copy")!;
        copy.Visibility.Should().Be(SheetVisibility.Hidden);
        copy.Cell("A1")!.IsGoogleOnly.Should().BeTrue();
        copy.Cell("A1")!.Number.Should().Be(7m);
    }

    [Fact]
    public void The_1904_date_system_is_honoured()
    {
        var writer = new XlsxWriter { Date1904 = true };
        writer.Sheet("S").Set("A1", new DateOnly(2026, 8, 7));

        var book = XlsxReader.Read(writer.ToBytes());

        book.Date1904.Should().BeTrue();
        book.ToDate(book.Sheet("S")!.Cell("A1")!.Number!.Value).Should().Be(new DateOnly(2026, 8, 7));
    }

    [Fact]
    public void Floating_point_noise_is_kept_exactly_as_stored()
    {
        var book = XlsxReader.Read(Package(Sheet("""<row r="1"><c r="A1"><v>1879.2400000000002</v></c></row>""")));

        book.Sheet("Sheet1")!.Cell("A1")!.Number.Should().Be(1879.2400000000002m);
    }

    [Fact]
    public void Inline_strings_rich_text_and_shared_formulas_are_read()
    {
        var sheet = Sheet("""
            <row r="1">
              <c r="A1" t="inlineStr"><is><t>Inline</t></is></c>
              <c r="B1" t="s"><v>0</v></c>
              <c r="C1"><f t="shared" ref="C1:C2" si="0">A2*2</f><v>2</v></c>
            </row>
            <row r="2"><c r="A2"><v>1</v></c><c r="C2"><f t="shared" si="0"/><v>4</v></c></row>
            """);
        var strings = """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><r><t>Rich </t></r><r><t>text</t></r><rPh><t>ignored</t></rPh></si></sst>""";

        var book = XlsxReader.Read(Package(sheet, strings));

        var s = book.Sheet("Sheet1")!;
        s.Cell("A1")!.Text.Should().Be("Inline");
        s.Cell("B1")!.Text.Should().Be("Rich text");
        s.Cell("C2")!.HasFormula.Should().BeTrue();
        s.Cell("C2")!.Number.Should().Be(4m);
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_refused()
    {
        var act = () => XlsxReader.Read(Encoding.UTF8.GetBytes("Date,Amount\n2026-10-01,12.50\n"));

        act.Should().Throw<XlsxFormatException>();
    }

    [Fact]
    public void A_zip_without_a_workbook_part_is_refused()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            writer.Write("hello");
        }

        var act = () => XlsxReader.Read(output.ToArray());

        act.Should().Throw<XlsxFormatException>().WithMessage("*workbook*");
    }

    [Fact]
    public void Document_type_definitions_are_refused()
    {
        var hostile = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>&b;</t></is></c></row></sheetData></worksheet>""";

        var act = () => XlsxReader.Read(Package(hostile));

        act.Should().Throw<XlsxFormatException>();
    }

    [Fact]
    public void Parts_that_expand_past_the_limit_are_refused()
    {
        var big = Sheet(string.Concat(Enumerable.Range(1, 2000).Select(r => $"""<row r="{r}"><c r="A{r}" t="inlineStr"><is><t>{new string('x', 200)}</t></is></c></row>""")));

        var act = () => XlsxReader.Read(Package(big), new XlsxLimits(MaxPartBytes: 64 * 1024));

        act.Should().Throw<XlsxFormatException>().WithMessage("*more data*");
    }

    [Theory]
    [InlineData("A1", 1, 1)]
    [InlineData("Z9", 9, 26)]
    [InlineData("AA10", 10, 27)]
    [InlineData("AH787", 787, 34)]
    public void Addresses_round_trip(string address, int row, int column)
    {
        XlsxAddress.Parse(address).Should().Be((row, column));
        XlsxAddress.Format(row, column).Should().Be(address);
    }

    private static string Sheet(string rows) =>
        $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>""";

    // A hand-built package, for shapes the writer doesn't produce.
    private static byte[] Package(string sheetXml, string? sharedStrings = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add(zip, "xl/worksheets/sheet1.xml", sheetXml);
            if (sharedStrings is not null)
            {
                Add(zip, "xl/sharedStrings.xml", sharedStrings);
            }
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
