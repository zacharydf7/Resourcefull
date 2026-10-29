using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Resourcefull.Tests.AspNetCore;

public class ResourcefullExceptionHandlerTests
{
    public static TheoryData<Exception, HttpStatusCode> MappedExceptions() => new()
    {
        { new ResourceConflictException("Resource report.txt already exists."), HttpStatusCode.Conflict },
        { new ResourceContainerNotFoundException("Resource Container files does not exist."), HttpStatusCode.NotFound },
        { new ResourceContentLengthMismatchException("The length doesn't match."), HttpStatusCode.BadRequest },
        { new ResourceNameException("Name must not start or end with whitespace.", "resourceName"), HttpStatusCode.BadRequest },
        { new ResourcePreconditionFailedException("Resource report.txt doesn't meet the conditions.", "v2", "sha256:current"), HttpStatusCode.PreconditionFailed },
        { new ResourcePolicyViolationException("Too large.", ResourcePolicyRule.MaxResourceSize, 10), HttpStatusCode.RequestEntityTooLarge },
        { new ResourcePolicyViolationException("No room.", ResourcePolicyRule.MaxContainerSize, 10), HttpStatusCode.RequestEntityTooLarge },
        { new ResourcePolicyViolationException("Too many.", ResourcePolicyRule.MaxResourceCount, 10), HttpStatusCode.Forbidden },
        { new ResourcePolicyViolationException("Not that type.", ResourcePolicyRule.AllowedContentTypes), HttpStatusCode.UnsupportedMediaType },
        { new ResourcePolicyViolationException("Read-only.", ResourcePolicyRule.ReadOnly), HttpStatusCode.Forbidden }
    };

    [Theory]
    [MemberData(nameof(MappedExceptions))]
    public async Task TryHandleAsync_ShouldAnswerWithTheStatusAndProblemDetails_WhenGivenAResourcefullException(Exception exception, HttpStatusCode status)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync(exception);

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/throw");

        // Assert
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        ProblemDetails problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(exception.Message, problem.Title);
        Assert.Equal((int)status, problem.Status);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldLeaveTheExceptionToTheDefaultHandler_WhenItIsNotResourcefulls()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync(new InvalidOperationException("Something else."));

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/throw");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
