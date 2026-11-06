using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>Upload links used by HTML forms: a <c>multipart/form-data</c> <c>POST</c> to <c>Results.SharedUpload</c>.</summary>
public class FormUploadLinkTests
{
    /// <summary>A form like a browser sends: a text field, then each file with its name and type.</summary>
    private static MultipartFormDataContent Form(params (string FileName, string ContentType, string Content)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent("Claim 1042"), "note" } };
        foreach ((string fileName, string contentType, string content) in files)
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            form.Add(file, "files", fileName);
        }
        return form;
    }

    private static async Task<string> TokenAsync(TestApp app, string? name = null, string? prefix = null, ResourceSize? maxSize = null, string[]? types = null) =>
        (await app.Links.CreateUploadTokenAsync(new ResourceUploadLink(TestApp.Container)
        {
            ResourceName = name, NamePrefix = prefix, MaxSize = maxSize, AllowedContentTypes = types
        }))!;

    private static async Task<string?> ReadAsync(TestApp app, string name) =>
        (await app.Files.DownloadContentAsync(name))?.Content.ToString();

    /// <summary>The names of the files the response lists as stored.</summary>
    private static async Task<string[]> StoredAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("stored").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
    }

    [Fact]
    public async Task SharedUpload_ShouldStoreEachFileUnderThePrefixWithItsType_WhenAFormPostsSeveral()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = await TokenAsync(app, prefix: "claims/1042/");

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{token}",
            Form(("photo.jpg", "image/jpeg", "one"), ("form.pdf", "application/pdf", "two")));

        // Assert: both files are stored, and listed with their ETags, by the names the form gave them.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] stored = [.. body.RootElement.GetProperty("stored").EnumerateArray()];
        Assert.Equal(["photo.jpg", "form.pdf"], stored.Select(f => f.GetProperty("name").GetString()));
        ResourceInfo photo = (await app.Files.GetResourceInfoAsync("claims/1042/photo.jpg"))!;
        Assert.Equal($"\"{photo.ContentHash}\"", stored[0].GetProperty("etag").GetString());
        Assert.Equal(("image/jpeg", "one"), (photo.ContentType, await ReadAsync(app, "claims/1042/photo.jpg")));
        Assert.Equal("two", await ReadAsync(app, "claims/1042/form.pdf"));
    }

    [Fact]
    public async Task SharedUpload_ShouldStoreTheFileUnderTheLinksName_WhenAFormPostsOneToALinkForOneName()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, name: "contract.pdf")}",
            Form(("signed scan.pdf", "application/pdf", "signed")));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["signed scan.pdf"], await StoredAsync(response));
        Assert.Equal("signed", await ReadAsync(app, "contract.pdf"));
    }

    [Theory]
    [InlineData("wrong-type", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("too-large", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("taken", HttpStatusCode.Conflict)]
    [InlineData("second-file", HttpStatusCode.BadRequest)]
    public async Task SharedUpload_ShouldStopAtTheFirstFileRefusedAndKeepThoseBefore_WhenAFormPostsSeveral(string problem,
        HttpStatusCode expected)
    {
        // Arrange: a form whose second file breaks one of the link's rules, or the store's, followed by a third.
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("in/taken.jpg", BinaryData.FromString("theirs"));
        string token = problem == "second-file"
            ? await TokenAsync(app, name: "in/first.jpg")
            : await TokenAsync(app, prefix: "in/", maxSize: 5, types: ["image/*"]);
        (string, string, string) second = problem switch
        {
            "wrong-type" => ("second.html", "text/html", "two"),
            "too-large" => ("second.jpg", "image/jpeg", "too large"),
            "taken" => ("taken.jpg", "image/jpeg", "two"),
            _ => ("second.jpg", "image/jpeg", "two")
        };

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{token}",
            Form(("first.jpg", "image/jpeg", "one"), second, ("third.jpg", "image/jpeg", "three")));

        // Assert: the answer names the file refused and lists the one stored before it, which stays, and nothing after
        // it is stored. It names no container.
        Assert.Equal(expected, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.Equal(second.Item1, body.RootElement.GetProperty("file").GetString());
        Assert.Equal(["first.jpg"], body.RootElement.GetProperty("stored").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.DoesNotContain("container", text);
        Assert.Equal("one", await ReadAsync(app, "in/first.jpg"));
        Assert.Equal(problem == "taken" ? "theirs" : null, await ReadAsync(app, "in/" + second.Item1));
        Assert.Null(await app.Files.GetResourceInfoAsync("in/third.jpg"));
    }

    [Fact]
    public async Task SharedUpload_ShouldNameTheLimitAndTheFile_WhenAFormPostsAFileOverALimitInUnits()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = await TokenAsync(app, prefix: "in/", maxSize: ResourceSize.Kilobytes(1));

        // Act
        HttpResponseMessage atLimit = await app.Client.PostAsync($"/upload/{token}",
            Form(("fits.jpg", "image/jpeg", new string('x', 1024))));
        HttpResponseMessage over = await app.Client.PostAsync($"/upload/{token}",
            Form(("big.jpg", "image/jpeg", new string('x', 1025))));

        // Assert
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Contains("at most 1 KB, and big.jpg is larger", await over.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerBadRequestListingWhatsStored_WhenTheFormIsCutShort()
    {
        // Arrange: a whole file, then a second that the body ends partway through, with no closing boundary.
        await using TestApp app = await TestApp.StartAsync();
        const string body = "--b\r\nContent-Disposition: form-data; name=\"files\"; filename=\"first.jpg\"\r\nContent-Type: image/jpeg\r\n\r\none"
            + "\r\n--b\r\nContent-Disposition: form-data; name=\"files\"; filename=\"second.jpg\"\r\nContent-Type: image/jpeg\r\n\r\ntw";
        var form = new StringContent(body);
        form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b");

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, prefix: "in/")}", form);

        // Assert: the first file stays, and is listed; the second isn't stored.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["first.jpg"], await StoredAsync(response));
        Assert.Equal("one", await ReadAsync(app, "in/first.jpg"));
        Assert.Null(await app.Files.GetResourceInfoAsync("in/second.jpg"));
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerBadRequestNamingNoFile_WhenTheFormIsCutShortInAFilesHeaders()
    {
        // Arrange: a whole file, then the start of a second's headers, where the body ends.
        await using TestApp app = await TestApp.StartAsync();
        var form = new StringContent("--b\r\nContent-Disposition: form-data; name=\"files\"; filename=\"first.jpg\"\r\nContent-Type: image/jpeg"
            + "\r\n\r\none\r\n--b\r\nContent-Disposition: form-da");
        form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b");

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, prefix: "in/")}", form);

        // Assert: the stored file is listed, and not named as the one refused.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["first.jpg"], await StoredAsync(response));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("file").ValueKind);
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerContentTooLargeListingWhatsStored_WhenTheFormIsLargerThanTheServerAccepts()
    {
        // Arrange: a server that accepts 9,000 bytes, more than the form reader reads ahead, and a form of two files whose
        // second goes past that limit.
        await using TestApp app = await TestApp.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/upload/{await TokenAsync(app, prefix: "in/")}")
        {
            Content = Form(("first.jpg", "image/jpeg", "one"), ("second.jpg", "image/jpeg", new string('x', 20_000)))
        };
        request.Headers.Add("X-Test-Body-Limit", "9000");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(["first.jpg"], await StoredAsync(response));
        Assert.Null(await app.Files.GetResourceInfoAsync("in/second.jpg"));
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerBadRequest_WhenTheFormsBoundaryIsLongerThan70Characters()
    {
        // Arrange: a form that's whole, but for its boundary, one character longer than RFC 2046 allows.
        await using TestApp app = await TestApp.StartAsync();
        string boundary = new('b', 71);
        var form = new StringContent($"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"a.jpg\"\r\n"
            + $"Content-Type: image/jpeg\r\n\r\none\r\n--{boundary}--\r\n");
        form.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, prefix: "in/")}", form);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await app.Files.ListResourcesAsync().ToListAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerBadRequest_WhenTheFormHasNoFile()
    {
        // Arrange: a form whose file input was left empty, which browsers send as a part with no file name.
        await using TestApp app = await TestApp.StartAsync();
        var empty = new ByteArrayContent([]);
        empty.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        empty.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"files\"", FileName = "\"\"" };
        MultipartFormDataContent form = Form();
        form.Add(empty);

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, prefix: "in/")}", form);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await app.Files.ListResourcesAsync().ToListAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerNotFound_WhenAFormIsPostedToANameOfItsOwn()
    {
        // Arrange: the form's files are named in the form, so the URL can't name one too.
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage response = await app.Client.PostAsync($"/upload/{await TokenAsync(app, prefix: "in/")}/photo.jpg",
            Form(("photo.jpg", "image/jpeg", "one")));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await app.Files.GetResourceInfoAsync("in/photo.jpg"));
    }

    [Fact]
    public async Task SharedUpload_ShouldStoreAFormAsTheFile_WhenItsPut()
    {
        // Arrange: only a POST is read as a form, so a PUT of a multipart body stores it as it is, as before.
        await using TestApp app = await TestApp.StartAsync();
        MultipartFormDataContent form = Form(("photo.jpg", "image/jpeg", "one"));
        string sent = await form.ReadAsStringAsync();

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{await TokenAsync(app, name: "message")}", form);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(sent, await ReadAsync(app, "message"));
    }
}
