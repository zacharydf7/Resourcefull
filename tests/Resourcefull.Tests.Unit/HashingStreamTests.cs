using System.Security.Cryptography;

namespace Resourcefull.Tests.Unit;

public class HashingStreamTests
{
    private const string EmptySha256 = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task Complete_ShouldReportTheCountAndSha256OfEveryByteRead_WhenTheStreamIsReadToTheEnd()
    {
        // Arrange
        byte[] payload = RandomNumberGenerator.GetBytes(200_000);
        using var content = new HashingStream(new MemoryStream(payload));

        // Act
        await content.CopyToAsync(Stream.Null);
        WrittenContent written = content.Complete(declaredLength: null);

        // Assert
        Assert.Equal(payload.Length, written.ContentLength);
        Assert.Equal(Sha256Of(payload), written.ContentHash);
    }

    [Fact]
    public void Complete_ShouldMatchASingleRead_WhenTheStreamIsReadInIrregularPieces()
    {
        // Arrange
        byte[] payload = RandomNumberGenerator.GetBytes(10_000);
        using var content = new HashingStream(new MemoryStream(payload));
        int[] pieceSizes = [1, 7, 4096, 3, 65535];
        var buffer = new byte[65535];

        // Act
        int bytesRead;
        int piece = 0;
        do
        {
            bytesRead = content.Read(buffer, 0, pieceSizes[piece++ % pieceSizes.Length]);
        }
        while (bytesRead > 0);

        WrittenContent written = content.Complete(declaredLength: null);

        // Assert
        Assert.Equal(payload.Length, written.ContentLength);
        Assert.Equal(Sha256Of(payload), written.ContentHash);
    }

    [Fact]
    public async Task Complete_ShouldReportZeroBytesAndTheEmptyHash_WhenTheStreamIsEmpty()
    {
        // Arrange
        using var content = new HashingStream(new MemoryStream());

        // Act
        await content.CopyToAsync(Stream.Null);
        WrittenContent written = content.Complete(declaredLength: 0);

        // Assert
        Assert.Equal(0, written.ContentLength);
        Assert.Equal(EmptySha256, written.ContentHash);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    public async Task Complete_ShouldThrow_WhenTheDeclaredLengthDiffersFromTheBytesRead(long declaredLength)
    {
        // Arrange & Act
        using var content = new HashingStream(new MemoryStream(new byte[10]));
        await content.CopyToAsync(Stream.Null);

        // Assert
        Assert.Throws<ResourceContentLengthMismatchException>(() => content.Complete(declaredLength));
    }

    private static string Sha256Of(byte[] payload) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
}
