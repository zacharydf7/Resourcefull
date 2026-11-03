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

    /// <summary>Names the rules from 4.0 refuse for a new name, though an earlier release could have stored them.</summary>
    public static TheoryData<string> NamesWithForbiddenCharacters() =>
    [
        "line\nbreak", "tab\tname", "bell\u0007", "delete\u007F", "invoice\u202Efdp.exe", "mark\u200Fhere", "isolate\u2066x",
        "arabic mark\u061C"
    ];

    /// <summary>Names the rules still allow: joiners, other format characters, and characters outside the BMP.</summary>
    public static TheoryData<string> NamesWithJoinersAndSurrogatePairs() =>
    [
        "family \uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67", "zero\u200Cwidth", "soft\u00ADhyphen", "party \uD83C\uDF89.png"
    ];

    [Theory]
    [MemberData(nameof(NamesWithForbiddenCharacters))]
    public void IsValidName_ShouldReturnFalse_WhenGivenAControlCharacterBidirectionalControlOrUnpairedSurrogate(string name)
    {
        Assert.False(ResourceNames.IsValidContainerName(name));
        Assert.False(ResourceNames.IsValidResourceName(name));
    }

    [Fact]
    public void IsValidName_ShouldReturnFalse_WhenGivenAnUnpairedSurrogate()
    {
        // In the test rather than as theory data, which xUnit serializes, replacing an unpaired surrogate with U+FFFD.
        string[] names = ["lone \uD800 high", "lone \uDC00 low", "ends high \uD83D", "\uDC00\uD800 reversed pair"];

        Assert.All(names, name => Assert.False(ResourceNames.IsValidContainerName(name)));
        Assert.All(names, name => Assert.False(ResourceNames.IsValidResourceName(name)));
    }

    [Theory]
    [MemberData(nameof(NamesWithJoinersAndSurrogatePairs))]
    public void IsValidName_ShouldReturnTrue_WhenGivenJoinersOrSurrogatePairs(string name)
    {
        Assert.True(ResourceNames.IsValidContainerName(name));
        Assert.True(ResourceNames.IsValidResourceName(name));
    }

    public static TheoryData<string> NonNullInvalidResourceNames() =>
        [.. ResourceContainerClientTests.InvalidResourceNames().Select(row => row.Data).OfType<string>()];

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
