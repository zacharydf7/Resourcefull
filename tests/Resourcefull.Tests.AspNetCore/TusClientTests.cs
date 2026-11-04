using System.Diagnostics;
using System.Text.Json;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>
/// Runs tus-js-client, the tus client most front ends use, in Node, against the resumable upload endpoints on a port of
/// their own, so what the protocol tests check is checked again with a real client's retries and resumption. They need
/// Node, and <c>npm ci</c> run in <c>tests/Resourcefull.Tests.AspNetCore/TusClient</c>; without them, they're skipped,
/// unless <c>RESOURCEFULL_TEST_TUS_CLIENT</c> is set, as CI sets it, and then they fail.
/// </summary>
public class TusClientTests
{
    private const string RequiredEnvVar = "RESOURCEFULL_TEST_TUS_CLIENT";

    private sealed record Summary(bool Ok, string? Url, string? Message, string Sha256, Request[] Requests);

    private sealed record Request(string Method, int? Status, string? Offset);

    /// <summary>The folder with the client script, once its packages are installed; skips the test otherwise.</summary>
    private static string ClientFolder()
    {
        string? folder = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Resourcefull.Tests.AspNetCore", "TusClient");
            if (File.Exists(Path.Combine(candidate, "upload.mjs")))
            {
                folder = candidate;
                break;
            }
        }
        bool installed = folder is not null && Directory.Exists(Path.Combine(folder, "node_modules", "tus-js-client"));
        string missing = "Run npm ci in tests/Resourcefull.Tests.AspNetCore/TusClient to run the tus-js-client tests.";
        if (Environment.GetEnvironmentVariable(RequiredEnvVar) is { Length: > 0 }) Assert.True(installed, missing);
        else Assert.SkipUnless(installed, missing);
        return folder!;
    }

    private static async Task<Summary> UploadAsync(string folder, Uri endpoint, string filename, string? scenario = null)
    {
        var start = new ProcessStartInfo("node") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "upload.mjs", endpoint.ToString(), filename, scenario ?? "" }) start.ArgumentList.Add(argument);
        using Process node = Process.Start(start)!;
        Task<string> output = node.StandardOutput.ReadToEndAsync();
        Task<string> errors = node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        Assert.True(node.ExitCode == 0, $"The client failed: {await errors}");
        return JsonSerializer.Deserialize<Summary>(await output, JsonSerializerOptions.Web)!;
    }

    [Fact]
    public async Task TusJsClient_ShouldResumeFromWhatsStoredAndFinish_WhenItsUploadIsInterrupted()
    {
        // Arrange
        string folder = ClientFolder();
        await using UploadApp app = await UploadApp.StartAsync(onPort: true);

        // Act
        Summary summary = await UploadAsync(folder, new Uri(app.Address!, "/uploads"), "resumed.bin", "interrupted");

        // Assert: after the interruption, the new client asked how much was stored, sent only the rest, and the whole
        // file is stored.
        Assert.True(summary.Ok, summary.Message);
        Request[] resumed = [.. summary.Requests.SkipWhile(r => r.Method != "ABORT").Skip(1)];
        Assert.Equal(("HEAD", 200, "1048576"), (resumed[0].Method, resumed[0].Status, resumed[0].Offset));
        Assert.DoesNotContain(resumed, r => r.Method == "POST");
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("tus/resumed.bin"))!;
        Assert.Equal("sha256:" + summary.Sha256, stored.ContentHash);
        Assert.Single(app.Completed);
    }

    [Fact]
    public async Task TusJsClient_ShouldReportAnError_WhenTheUploadCantBeCompleted()
    {
        // Arrange: every completion fails, as when the store can't be reached, through all of the client's retries.
        string folder = ClientFolder();
        await using UploadApp app = await UploadApp.StartAsync(onPort: true);
        app.FailCompletions = 100;

        // Act
        Summary summary = await UploadAsync(folder, new Uri(app.Address!, "/flaky-uploads"), "unstored.bin");

        // Assert: the client never reported success, since nothing was stored, and kept resuming the one upload, which
        // holds every byte, rather than sending the file again.
        Assert.False(summary.Ok);
        Assert.Contains(summary.Requests, r => r is { Method: "PATCH", Status: 503 });
        Assert.Contains(summary.Requests, r => r is { Method: "HEAD", Status: 423 });
        Assert.Single(summary.Requests, r => r.Method == "POST");
        Assert.Null(await app.Files.GetResourceInfoAsync("tus/unstored.bin"));
    }

    [Fact]
    public async Task TusJsClient_ShouldReportAnErrorWithoutSendingTheFileAgain_WhenTheNameIsTakenBeforeTheUploadCompletes()
    {
        // Arrange: another upload takes the name just before this one completes.
        string folder = ClientFolder();
        await using UploadApp app = await UploadApp.StartAsync(onPort: true);
        app.TakeNameBeforeCompleting = true;

        // Act
        Summary summary = await UploadAsync(folder, new Uri(app.Address!, "/flaky-uploads"), "overtaken.bin");

        // Assert: the client reported an error, the name keeps the other upload, and the file was sent once: the upload
        // was canceled, so the client started a new one, which was refused before any of the file was sent.
        Assert.False(summary.Ok);
        Assert.Equal(4, summary.Requests.Count(r => r.Method == "PATCH"));
        Assert.Contains(summary.Requests, r => r is { Method: "PATCH", Status: 409 });
        Request[] posts = [.. summary.Requests.Where(r => r.Method == "POST")];
        Assert.True(posts.Length > 1, "The client didn't start a new upload after the first was canceled.");
        Assert.All(posts.Skip(1), r => Assert.Equal(409, r.Status));
        Assert.Equal("theirs", (await app.Files.DownloadContentAsync("tus/overtaken.bin"))!.Content.ToString());
        Assert.Empty(app.Completed);
    }

    [Fact]
    public async Task TusJsClient_ShouldReportAnErrorBeforeSendingTheFile_WhenTheNameIsTaken()
    {
        // Arrange
        string folder = ClientFolder();
        await using UploadApp app = await UploadApp.StartAsync(onPort: true);
        await app.Files.UploadAsync("tus/taken.bin", BinaryData.FromString("theirs"));

        // Act
        Summary summary = await UploadAsync(folder, new Uri(app.Address!, "/uploads"), "taken.bin");

        // Assert
        Assert.False(summary.Ok);
        Assert.All(summary.Requests, r => Assert.Equal(("POST", 409), (r.Method, r.Status)));
        Assert.Equal("theirs", (await app.Files.DownloadContentAsync("tus/taken.bin"))!.Content.ToString());
    }
}
