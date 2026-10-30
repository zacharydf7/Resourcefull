using System.IO.Compression;

namespace Resourcefull.Tests.Unit;

public class RewindableEntryStreamTests
{
    private static async Task<ZipArchive> ZipOfAsync(byte[] content)
    {
        var zip = new MemoryStream();
        await using (ZipArchive writer = await ZipArchive.CreateAsync(zip, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null))
        await using (Stream entry = await writer.CreateEntry("content/a").OpenAsync())
            await entry.WriteAsync(content);
        zip.Position = 0;
        return await ZipArchive.CreateAsync(zip, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null);
    }

    [Fact]
    public async Task Seek_ShouldReadTheEntryAgainFromItsStart_WhenSoughtToTheStartAfterReading()
    {
        // Arrange
        byte[] content = [1, 2, 3, 4, 5];
        await using ZipArchive zip = await ZipOfAsync(content);
        ZipArchiveEntry entry = zip.GetEntry("content/a")!;
        await using var stream = new RewindableEntryStream(entry, await entry.OpenAsync(), content.Length);
        var first = new MemoryStream();
        await stream.CopyToAsync(first);

        // Act
        stream.Position = 0;
        var second = new MemoryStream();
        await stream.CopyToAsync(second);

        // Assert
        Assert.Equal(content, first.ToArray());
        Assert.Equal(content, second.ToArray());
        Assert.Equal(5, stream.Position);
    }

    [Theory]
    [InlineData(1L, SeekOrigin.Begin)]
    [InlineData(0L, SeekOrigin.End)]
    public async Task Seek_ShouldThrowNotSupported_WhenNotSoughtToTheStart(long offset, SeekOrigin origin)
    {
        // Arrange
        await using ZipArchive zip = await ZipOfAsync([1, 2, 3]);
        ZipArchiveEntry entry = zip.GetEntry("content/a")!;
        await using var stream = new RewindableEntryStream(entry, await entry.OpenAsync(), 3);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Seek(offset, origin));
    }

    [Fact]
    public async Task Read_ShouldThrowInvalidData_WhenTheEntryIsLongerThanItsDeclaredLength()
    {
        // Arrange
        await using ZipArchive zip = await ZipOfAsync(new byte[1000]);
        ZipArchiveEntry entry = zip.GetEntry("content/a")!;
        await using var stream = new RewindableEntryStream(entry, await entry.OpenAsync(), 10);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.CopyToAsync(Stream.Null));
    }
}
