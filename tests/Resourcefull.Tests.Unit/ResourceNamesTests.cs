namespace Resourcefull.Tests.Unit;

public class ResourceNamesTests
{
    public static TheoryData<string> ValidContainerNames() => ["a", "my container", "2026/10", new string('a', ResourceNames.MaxContainerNameLength)];

    public static TheoryData<string> ValidResourceNames() => ["a", "my report.pdf", "2026/10/report.pdf", new string('a', ResourceNames.MaxResourceNameLength)];

    [Theory]
    [MemberData(nameof(ResourceServiceClientTests.InvalidContainerNames), MemberType = typeof(ResourceServiceClientTests))]
    public void IsValidContainerName_ShouldReturnFalse_WhenGivenANameTheClientsReject(string? name)
        => Assert.False(ResourceNames.IsValidContainerName(name));

    [Theory]
    [MemberData(nameof(ValidContainerNames))]
    public void IsValidContainerName_ShouldReturnTrue_WhenGivenAValidName(string name)
        => Assert.True(ResourceNames.IsValidContainerName(name));

    [Theory]
    [MemberData(nameof(ResourceContainerClientTests.InvalidResourceNames), MemberType = typeof(ResourceContainerClientTests))]
    public void IsValidResourceName_ShouldReturnFalse_WhenGivenANameTheClientsReject(string? name)
        => Assert.False(ResourceNames.IsValidResourceName(name));

    [Theory]
    [MemberData(nameof(ValidResourceNames))]
    public void IsValidResourceName_ShouldReturnTrue_WhenGivenAValidName(string name)
        => Assert.True(ResourceNames.IsValidResourceName(name));

    public static TheoryData<string> NonNullInvalidResourceNames() =>
        [.. ((IEnumerable<object?[]>)ResourceContainerClientTests.InvalidResourceNames()).Select(row => row[0]).OfType<string>()];

    [Theory]
    [MemberData(nameof(NonNullInvalidResourceNames))]
    public void ThrowIfInvalidResourceName_ShouldThrowResourceNameExceptionNamingTheArgument_WhenGivenANonNullInvalidName(string name)
    {
        var exception = Assert.Throws<ResourceNameException>(() => ResourceNames.ThrowIfInvalidResourceName(name, "resourceName"));
        Assert.Equal(("resourceName", name), (exception.ParamName, exception.Name));
    }

    [Fact]
    public void ThrowIfInvalidResourceName_ShouldThrowArgumentNullException_WhenGivenNull()
        => Assert.Throws<ArgumentNullException>("resourceName", () => ResourceNames.ThrowIfInvalidResourceName(null!, "resourceName"));
}
