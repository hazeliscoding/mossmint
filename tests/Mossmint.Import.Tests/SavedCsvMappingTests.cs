using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

public sealed class SavedCsvMappingTests
{
    [Fact]
    public void Round_trips_the_mapping_and_where_each_account_goes()
    {
        var saved = new SavedCsvMapping(
            new CsvMapping(2, "d/M/yyyy", 1, AmountStyle.SeparateColumns, MoneyOut: 3, MoneyIn: 4, Account: 0, DecimalSeparator: ','),
            new Dictionary<string, Guid> { ["Checking"] = Guid.Parse("0199d2a0-0000-7000-8000-000000000001") });

        var back = SavedCsvMapping.FromJson(saved.ToJson());

        back!.Mapping.Should().Be(saved.Mapping);
        back.Destinations.Should().BeEquivalentTo(saved.Destinations);
        saved.ToJson().Should().Contain("\"SeparateColumns\"", "styles are stored by name so reordering the enum can't change a saved mapping");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":99}")]
    public void A_mapping_it_can_not_read_is_ignored(string json) => SavedCsvMapping.FromJson(json).Should().BeNull();
}
