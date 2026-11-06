using System.Reflection;

namespace Resourcefull.Tests.Unit;

public class TypeShapeTests
{
    private static readonly string[] Packages =
    [
        "Resourcefull", "Resourcefull.AspNetCore", "Resourcefull.AzureBlob", "Resourcefull.FileSystem", "Resourcefull.InMemory",
        "Resourcefull.PostgreSql", "Resourcefull.S3", "Resourcefull.Sqlite", "Resourcefull.SqlServer", "Resourcefull.Testing"
    ];

    private static IEnumerable<Type> PublicRecords() => Packages
        .SelectMany(package => Assembly.Load(package).GetExportedTypes())
        .Where(type => type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic) is not null);

    [Fact]
    public void PublicRecords_ShouldTakeNoPositionalParameters_WhenTheyAreNotAnIdentity()
    {
        // Arrange: records that are an identity, where a new field would make them a different thing, and records that
        // take only their identity by position and the rest as properties, which can grow.
        string[] identities =
        [
            "NewResourceVersion", "ResourceAddress", "ResourceLink", "ResourceMetadataMatch", "ResourceOrigin", "ResourceRange",
            "ResourceUploadLink", "ResourceUploadTarget", "ResourceUriContext", "ResourceVersionAddress", "ResourceVersionKey"
        ];

        // Act
        string[] positional = [.. PublicRecords().Where(type => type.GetMethod("Deconstruct") is not null).Select(type => type.Name).Order(StringComparer.Ordinal)];

        // Assert: a positional record gains a member only by breaking its constructor and Deconstruct.
        Assert.Equal(identities, positional);
    }

    [Fact]
    public void PublicRecordClasses_ShouldBeSealed_WhenCalled()
    {
        // Act
        IEnumerable<string> unsealed = PublicRecords().Where(type => type.IsClass && !type.IsSealed).Select(type => type.Name);

        // Assert: a record left unsealed can't be sealed after 4.0, and deriving from one has no use here.
        Assert.Empty(unsealed);
    }

    [Theory]
    [InlineData(typeof(ResourceDownloadResult))]
    [InlineData(typeof(ResourcefullOptions))]
    [InlineData(typeof(ResourcefullOptionsBuilder))]
    [InlineData(typeof(ResourceUploadOptions))]
    public void Type_ShouldBeSealed_WhenGivenOneNothingDerivesFrom(Type type) => Assert.True(type.IsSealed, type.Name);

    [Fact]
    public void ResourceDownloadResult_ShouldNeedTheTotalLengthAndNotBeARecord_WhenCalled()
    {
        // Act
        ConstructorInfo constructor = Assert.Single(typeof(ResourceDownloadResult).GetConstructors());

        // Assert: a copy would be a second owner of the stream.
        Assert.Equal(["resourceInfo", "stream", "totalLength"], constructor.GetParameters().Select(p => p.Name));
        Assert.DoesNotContain(typeof(ResourceDownloadResult).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic), m => m.Name == "PrintMembers");
    }

    [Fact]
    public void Services_ShouldHaveNoSetter_WhenOnTheOptionsBuilder() =>
        Assert.Null(typeof(ResourcefullOptionsBuilder).GetProperty(nameof(ResourcefullOptionsBuilder.Services))!.SetMethod);
}
