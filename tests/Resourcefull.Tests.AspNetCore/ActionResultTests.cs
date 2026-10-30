using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>The results run as MVC action results, as they do when a Razor Pages handler returns one.</summary>
public class ActionResultTests
{
    private static async Task<TestApp> StartWithDigitsAsync()
    {
        TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("digits.txt", BinaryData.FromString("0123456789"), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        return app;
    }

    private static async Task<(HttpResponse Response, string Body)> ExecuteAsync(TestApp app, IActionResult result)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services.CreateScope().ServiceProvider };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        return (context.Response, System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task ResourceHttpResult_ShouldSendTheDownload_WhenRunAsAnActionResult()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        (HttpResponse response, string body) = await ExecuteAsync(app, TypedResults.Resource((await app.Files.DownloadStreamingAsync("digits.txt"))!));

        // Assert
        Assert.Equal((StatusCodes.Status200OK, "0123456789"), (response.StatusCode, body));
        Assert.Equal("text/plain", response.ContentType);
    }

    [Fact]
    public async Task RazorPage_ShouldSendTheRange_WhenItsHandlerReturnsTheResult()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/page/digits.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4);

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("234", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SharedResourceHttpResult_ShouldSendTheLinkedResource_WhenRunAsAnActionResult()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        string token = (await app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "digits.txt")))!;
        IResourceServiceClient resources = app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>();

        // Act
        (HttpResponse response, string body) = await ExecuteAsync(app, TypedResults.SharedResource(resources, token));

        // Assert
        Assert.Equal((StatusCodes.Status200OK, "0123456789"), (response.StatusCode, body));
    }
}
