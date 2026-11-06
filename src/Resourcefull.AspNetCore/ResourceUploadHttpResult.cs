using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Resourcefull.AspNetCore;

/// <summary>
/// What <see cref="ResourceContainerClientHttpExtensions.UploadFromRequestAsync"/> did with a request's body, and the
/// answer to send: 201 Created when the name had no current version, and 200 OK when the upload replaced one, either way
/// with the stored version as JSON; or 412 Precondition Failed with problem details, when the request's preconditions
/// don't hold. The response's <c>ETag</c> is already set when you get this result: the stored version's, or on a 412,
/// the current version's if it's known. Return it from a minimal API endpoint, a controller action or a Razor Pages
/// handler.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceUploadHttpResult : IResult, IActionResult, IStatusCodeHttpResult
{
    private readonly IResult _answer;

    internal ResourceUploadHttpResult(int statusCode, ResourceInfo? resource, IResult answer)
    {
        StatusCode = statusCode;
        Resource = resource;
        _answer = answer;
    }

    /// <summary>The status code the answer has: 201, 200 or 412.</summary>
    public int StatusCode { get; }

    /// <inheritdoc />
    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    /// <summary>The stored version; <see langword="null"/> on 412 Precondition Failed, when nothing was stored.</summary>
    public ResourceInfo? Resource { get; }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext) => _answer.ExecuteAsync(httpContext);

    /// <inheritdoc />
    Task IActionResult.ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
