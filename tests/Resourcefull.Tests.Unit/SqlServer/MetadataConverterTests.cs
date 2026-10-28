using Resourcefull.SqlServer.Converters;

namespace Resourcefull.Tests.Unit.SqlServer;

public class MetadataConverterTests
{
    [Fact]
    public void MetadataConverter_ShouldRoundTripMetadata_WhenDictionaryHasEntries()
    {
        // Arrange
        var converter = new MetadataConverter();
        IDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["author"] = "zach",
            ["env"] = "prod"
        };

        // Act
        var serialized = (string)converter.ConvertToProvider(metadata)!;
        var deserialized = (IDictionary<string, string>)converter.ConvertFromProvider(serialized)!;

        // Assert
        Assert.Equal(metadata, deserialized);
    }

    [Fact]
    public void MetadataConverter_ShouldRoundTripMetadata_WhenDictionaryIsEmpty()
    {
        // Arrange
        var converter = new MetadataConverter();

        // Act
        var serialized = (string)converter.ConvertToProvider(new Dictionary<string, string>())!;
        var deserialized = (IDictionary<string, string>)converter.ConvertFromProvider(serialized)!;

        // Assert
        Assert.Empty(deserialized);
    }
}
