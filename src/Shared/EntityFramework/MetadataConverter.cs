using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Resourcefull.SqlServer.Converters;
internal class MetadataConverter : ValueConverter<IDictionary<string, string>?, string>
{
    private static readonly JsonSerializerOptions _options = new();

    public MetadataConverter() : base(
        v => JsonSerializer.Serialize(v, _options),
        v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, _options))
    {
    }
}