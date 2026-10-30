using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    /// <summary>A stream that can only be read or written forward, as an HTTP request or response body is.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        // Refused, as ASP.NET Core refuses them on a response body, so an export that works here works there.
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task<MemoryStream> ExportAsync(IResourceServiceClient service, string containerName)
    {
        var export = new MemoryStream();
        await service.ExportContainerAsync(containerName, export);
        export.Position = 0;
        return export;
    }

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldRestoreEveryVersionAsItWas_WhenGivenAnExport()
    {
        // Arrange: metadata, a history over several chunks, a copy, an expiry, and an older version that's current.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        string sourceName = NewContainerName(), restoredName = NewContainerName();
        IResourceContainerClient source = await service.CreateResourceContainerAsync(sourceName, new Dictionary<string, string> { ["team"] = "billing" });
        byte[] large = RandomNumberGenerator.GetBytes(150_000);
        await source.UploadAsync("report.pdf", new MemoryStream(large), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/pdf", null),
            Metadata = new Dictionary<string, string> { ["author"] = "ada" },
            ExpiresAt = Future
        });
        await source.UploadAsync("report.pdf", BinaryData.FromString("second"));
        await source.CopyResourceAsync("report.pdf", sourceName, "copy.pdf", new ResourceCopyOptions
        {
            VersionId = (await source.GetResourceVersionsAsync("report.pdf"))[1].VersionId
        });

        // Act
        ResourceImportResult imported = await service.ImportContainerAsync(restoredName, await ExportAsync(service, sourceName));

        // Assert
        IResourceContainerClient restored = service.GetResourceContainerClient(restoredName);
        Assert.Equal("billing", (await service.GetResourceContainerInfoAsync(restoredName))!.Metadata["team"]);
        Assert.Equal((2L, 3L), (imported.ResourceCount, imported.VersionCount));
        foreach (string name in new[] { "report.pdf", "copy.pdf" })
        {
            IReadOnlyList<ResourceInfo> before = await source.GetResourceVersionsAsync(name);
            IReadOnlyList<ResourceInfo> after = await restored.GetResourceVersionsAsync(name);
            Assert.Equal(before.Select(Describe), after.Select(Describe));
        }
        Assert.Equal(large, (await restored.DownloadContentAsync("copy.pdf"))!.Content.ToArray());
        Assert.Equal("second", (await restored.DownloadContentAsync("report.pdf"))!.Content.ToString());

        static object Describe(ResourceInfo v) => (v.VersionId, v.LastModified, v.IsLatest, v.ContentType, v.ContentLength, v.ContentHash,
            string.Join(",", v.Metadata.Select(m => $"{m.Key}={m.Value}")), v.CopiedFrom, v.ExpiresAt);
    }

    [SkippableFact]
    public async Task ExportContainerAsync_ShouldWriteEachDistinctContentOnce_WhenVersionsShareIt()
    {
        // Arrange: the same content uploaded twice and copied once.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient container = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        await container.UploadAsync("a", BinaryData.FromString("same"));
        await container.UploadAsync("a", BinaryData.FromString("same"));
        await container.CopyResourceAsync("a", name, "b");
        await container.UploadAsync("c", BinaryData.FromString("different"));

        // Act
        var export = new MemoryStream();
        ResourceExportResult exported = await service.ExportContainerAsync(name, new ForwardOnlyStream(export));

        // Assert: written to a stream that can't seek, with two contents for four versions.
        export.Position = 0;
        await using ZipArchive zip = await ZipArchive.CreateAsync(export, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("content/", StringComparison.Ordinal)));
        Assert.Equal((3L, 4L, 13L), (exported.ResourceCount, exported.VersionCount, exported.ContentSize.Bytes));
    }

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldBeRefusedAndChangeNothing_WhenTheContainerExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        IResourceContainerClient existing = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("x"));
        await existing.UploadAsync("kept", BinaryData.FromString("y"));

        // Act
        Task act() => service.ImportContainerAsync(existing.ResourceContainerInfo.Name,
            ExportAsync(service, source.ResourceContainerInfo.Name).GetAwaiter().GetResult());

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal(["kept"], (await existing.GetResourcesAsync()).Select(r => r.Name));
    }

    [SkippableTheory]
    [InlineData("tampered")]
    [InlineData("changed to another length")]
    public async Task ImportContainerAsync_ShouldRefuseAndLeaveNoContainer_WhenTheExportsContentWasChanged(string replacement)
    {
        // Arrange: an export whose content was edited after it was written.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("original"));
        await source.UploadAsync("b", BinaryData.FromString("intact"));
        MemoryStream export = await ExportAsync(service, source.ResourceContainerInfo.Name);
        await using (ZipArchive zip = await ZipArchive.CreateAsync(export, ZipArchiveMode.Update, leaveOpen: true, entryNameEncoding: null))
        {
            ZipArchiveEntry content = zip.Entries.First(e => e.FullName.StartsWith("content/", StringComparison.Ordinal));
            await using Stream stream = await content.OpenAsync();
            stream.SetLength(0);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(replacement));
        }
        export.Position = 0;
        string restoredName = NewContainerName();

        // Act
        Task act() => service.ImportContainerAsync(restoredName, export);

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(act);
        Assert.False(await service.ContainerExistsAsync(restoredName));
    }

    [SkippableTheory]
    [InlineData("\"name\": \"a\"", "\"name\": \"a \"")]                      // a name the rules refuse
    [InlineData("\"name\": \"b\"", "\"name\": \"a\"")]                       // the same name twice
    [InlineData("\"contentType\": null,", "")]                               // a field left out
    public async Task ImportContainerAsync_ShouldRefuseBeforeCreatingAnything_WhenTheManifestWasEdited(string original, string edited)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("x"));
        await source.UploadAsync("b", BinaryData.FromString("y"));
        MemoryStream export = await ExportAsync(service, source.ResourceContainerInfo.Name);
        await using (ZipArchive zip = await ZipArchive.CreateAsync(export, ZipArchiveMode.Update, leaveOpen: true, entryNameEncoding: null))
        {
            ZipArchiveEntry manifest = zip.GetEntry("manifest.json")!;
            string text;
            await using (Stream read = await manifest.OpenAsync())
                text = await new StreamReader(read).ReadToEndAsync();
            Assert.Contains(original, text);
            int at = text.IndexOf(original, StringComparison.Ordinal);
            await using Stream write = await manifest.OpenAsync();
            write.SetLength(0);
            await write.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text[..at] + edited + text[(at + original.Length)..]));
        }
        export.Position = 0;
        string restoredName = NewContainerName();

        // Act
        Task act() => service.ImportContainerAsync(restoredName, export);

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(act);
        Assert.False(await service.ContainerExistsAsync(restoredName));
    }

    [SkippableFact]
    public async Task ImportVersionAsync_ShouldRefuseAVersionIdTheNameAlreadyHas_WhenCalledTwice()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        var version = new ResourceInfo { ContainerName = container.ResourceContainerInfo.Name, Name = "a", VersionId = "imported", LastModified = Past };
        await repository.ImportVersionAsync(version.ContainerName, version, new MemoryStream([1]));

        // Act
        Task again() => repository.ImportVersionAsync(version.ContainerName, version, new MemoryStream([2]));

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(again);
        ResourceInfo stored = (await container.GetResourceInfoAsync("a"))!;
        Assert.Equal(("imported", Past), (stored.VersionId, stored.LastModified));
    }

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldRefuseBeforeCreatingAnything_WhenTheFileIsNotAnExport()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        var notAZip = new MemoryStream("just some text"u8.ToArray());
        var zipWithoutManifest = new MemoryStream();
        await using (ZipArchive zip = await ZipArchive.CreateAsync(zipWithoutManifest, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null))
            zip.CreateEntry("readme.txt");
        zipWithoutManifest.Position = 0;
        string first = NewContainerName(), second = NewContainerName();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportContainerAsync(first, notAZip));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportContainerAsync(second, zipWithoutManifest));
        Assert.False(await service.ContainerExistsAsync(first));
        Assert.False(await service.ContainerExistsAsync(second));
    }

    [SkippableFact]
    public async Task ExportAndImport_ShouldThrow_WhenTheContainerIsMissingOrTheSourceCantSeek()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("x"));
        MemoryStream export = await ExportAsync(service, source.ResourceContainerInfo.Name);

        // Act & Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => service.ExportContainerAsync(NewContainerName(), new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportContainerAsync(NewContainerName(), new ForwardOnlyStream(export)));
    }

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldApplyThePolicyAndLeaveNoContainer_WhenItRefusesAVersion()
    {
        // Arrange: the container imported into may hold only small files.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("small", BinaryData.FromString("x"));
        await source.UploadAsync("large", new MemoryStream(new byte[100]));
        string restoredName = NewContainerName();
        TestPolicyProvider.Policies[restoredName] = new ResourceContainerPolicy { MaxResourceSize = 50 };

        // Act
        Task act() => service.ImportContainerAsync(restoredName, ExportAsync(service, source.ResourceContainerInfo.Name).GetAwaiter().GetResult());

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(act)).Rule);
        Assert.False(await service.ContainerExistsAsync(restoredName));
    }

    [SkippableTheory]
    [InlineData(ResourcePolicyRule.MaxResourceCount, 2L, true)]     // "a" twice and "b": two names
    [InlineData(ResourcePolicyRule.MaxResourceCount, 1L, false)]
    [InlineData(ResourcePolicyRule.MaxContainerSize, 30L, true)]    // three versions of 10 bytes
    [InlineData(ResourcePolicyRule.MaxContainerSize, 29L, false)]
    public async Task ImportContainerAsync_ShouldCountWhatItHasImported_WhenTheContainerHasALimit(ResourcePolicyRule rule, long limit, bool fits)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning().UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", new MemoryStream(new byte[10]));
        await source.UploadAsync("a", new MemoryStream(new byte[10]));
        await source.UploadAsync("b", new MemoryStream(new byte[10]));
        MemoryStream export = await ExportAsync(service, source.ResourceContainerInfo.Name);
        string restoredName = NewContainerName();
        TestPolicyProvider.Policies[restoredName] = rule == ResourcePolicyRule.MaxResourceCount
            ? new ResourceContainerPolicy { MaxResourceCount = limit }
            : new ResourceContainerPolicy { MaxContainerSize = limit };

        // Act
        Exception? refused = await Record.ExceptionAsync(() => service.ImportContainerAsync(restoredName, export));

        // Assert
        Assert.Equal(fits, refused is null);
        if (!fits) Assert.Equal(rule, Assert.IsType<ResourcePolicyViolationException>(refused).Rule);
        Assert.Equal(fits, await service.ContainerExistsAsync(restoredName));
    }

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldNotCountANameWithNoVersions_WhenTheContainerLimitsNames()
    {
        // Arrange: an export of "a" and "b" whose manifest also lists a name with no versions, which stores nothing.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("x"));
        await source.UploadAsync("b", BinaryData.FromString("y"));
        MemoryStream export = await ExportAsync(service, source.ResourceContainerInfo.Name);
        await using (ZipArchive zip = await ZipArchive.CreateAsync(export, ZipArchiveMode.Update, leaveOpen: true, entryNameEncoding: null))
        {
            ZipArchiveEntry manifest = zip.GetEntry("manifest.json")!;
            string text;
            await using (Stream read = await manifest.OpenAsync())
                text = await new StreamReader(read).ReadToEndAsync();
            Assert.Contains("\"resources\": [", text);
            await using Stream write = await manifest.OpenAsync();
            write.SetLength(0);
            await write.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
                text.Replace("\"resources\": [", "\"resources\": [ { \"name\": \"empty\", \"versions\": [] },")));
        }
        export.Position = 0;
        string restoredName = NewContainerName();
        TestPolicyProvider.Policies[restoredName] = new ResourceContainerPolicy { MaxResourceCount = 2 };

        // Act
        ResourceImportResult imported = await service.ImportContainerAsync(restoredName, export);

        // Assert
        Assert.Equal(2, (await service.GetResourceContainerClient(restoredName).GetUsageAsync()).ResourceCount);
        Assert.Equal(2, imported.VersionCount);
    }

    /// <summary>
    /// A format 1 export as 3.2 writes it: a copy, a name with two versions that share their content, a content type,
    /// metadata with mixed-case keys, and an expiry. Exports already written have to stay readable, so this text must
    /// never change; a new format gets a test of its own.
    /// </summary>
    private const string Format1Manifest = """
        {
          "format": 1,
          "metadata": { "Team": "billing" },
          "resources": [
            {
              "name": "copy.txt",
              "versions": [
                {
                  "versionId": "0193a1b2-0000-7000-8000-000000000003",
                  "lastModified": "2026-01-02T03:04:07+00:00",
                  "contentType": null,
                  "contentLength": 5,
                  "contentHash": "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                  "metadata": {},
                  "copiedFrom": { "containerName": "notes", "resourceName": "notes.txt", "versionId": "0193a1b2-0000-7000-8000-000000000001" },
                  "expiresAt": null,
                  "content": "content/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
                }
              ]
            },
            {
              "name": "notes.txt",
              "versions": [
                {
                  "versionId": "0193a1b2-0000-7000-8000-000000000001",
                  "lastModified": "2026-01-02T03:04:05+00:00",
                  "contentType": "text/plain",
                  "contentLength": 5,
                  "contentHash": "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                  "metadata": { "Author": "ada" },
                  "copiedFrom": null,
                  "expiresAt": null,
                  "content": "content/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
                },
                {
                  "versionId": "0193a1b2-0000-7000-8000-000000000002",
                  "lastModified": "2026-01-02T03:04:06+00:00",
                  "contentType": "text/plain",
                  "contentLength": 5,
                  "contentHash": "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                  "metadata": {},
                  "copiedFrom": null,
                  "expiresAt": "2099-12-31T00:00:00+00:00",
                  "content": "content/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
                }
              ]
            }
          ]
        }
        """;

    [SkippableFact]
    public async Task ImportContainerAsync_ShouldReadAFormat1ExportAndExportItAgainUnchanged_WhenGivenOneAs32WroteIt()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        var golden = new MemoryStream();
        await using (ZipArchive zip = await ZipArchive.CreateAsync(golden, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null))
        {
            await using (Stream manifest = await zip.CreateEntry("manifest.json").OpenAsync())
                await manifest.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Format1Manifest));
            const string hello = "content/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
            await using Stream content = await zip.CreateEntry(hello).OpenAsync();
            await content.WriteAsync("hello"u8.ToArray());
        }
        golden.Position = 0;
        string name = NewContainerName();

        // Act
        await service.ImportContainerAsync(name, golden);
        MemoryStream exported = await ExportAsync(service, name);

        // Assert: what was read, and that writing it again gives the same manifest.
        IResourceContainerClient container = service.GetResourceContainerClient(name);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("notes.txt");
        Assert.Equal(["0193a1b2-0000-7000-8000-000000000002", "0193a1b2-0000-7000-8000-000000000001"],
            versions.Select(v => v.VersionId));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero), versions[0].LastModified);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero), versions[0].ExpiresAt);
        Assert.Equal("ada", versions[1].Metadata["Author"]);
        Assert.Equal("text/plain", versions[1].ContentType);
        Assert.Equal(new ResourceOrigin("notes", "notes.txt", "0193a1b2-0000-7000-8000-000000000001"),
            (await container.GetResourceInfoAsync("copy.txt"))!.CopiedFrom);
        Assert.Equal("hello", (await container.DownloadContentAsync("copy.txt"))!.Content.ToString());
        await using ZipArchive written = await ZipArchive.CreateAsync(exported, ZipArchiveMode.Read, leaveOpen: false,
            entryNameEncoding: null);
        string manifestText;
        await using (Stream read = await written.GetEntry("manifest.json")!.OpenAsync())
            manifestText = await new StreamReader(read).ReadToEndAsync();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Format1Manifest), JsonNode.Parse(manifestText)),
            manifestText);
    }
}
