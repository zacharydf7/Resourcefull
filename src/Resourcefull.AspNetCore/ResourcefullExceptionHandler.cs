using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Turns Resourcefull's exceptions into problem details responses: <see cref="ResourceConflictException"/> into 409,
/// <see cref="ResourceContainerNotFoundException"/> into 404, <see cref="ResourceContentLengthMismatchException"/> and
/// <see cref="ResourceNameException"/> into 400, and <see cref="ResourcePreconditionFailedException"/> into 412. Their
/// messages name the resource or container, and sometimes the argument or option involved, but nothing about your
/// infrastructure, so they're used as the title.
/// Any other exception is left to the next handler. (ASP.NET Core removes the <c>ETag</c> header from responses its
/// exception handling writes, so a 412 from here doesn't carry the current version's; the 412 that
/// <see cref="ResourceContainerClientHttpExtensions.UploadFromRequestAsync"/> returns itself does.)
/// </summary>
public sealed class ResourcefullExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int? status = exception switch
        {
            ResourceConflictException => StatusCodes.Status409Conflict,
            ResourceContainerNotFoundException => StatusCodes.Status404NotFound,
            ResourceContentLengthMismatchException or ResourceNameException => StatusCodes.Status400BadRequest,
            ResourcePreconditionFailedException => StatusCodes.Status412PreconditionFailed,
            _ => null
        };
        if (status is null) return false;

        httpContext.Response.StatusCode = status.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = exception.Message }
        });
    }
}

/// <summary>Registers <see cref="ResourcefullExceptionHandler"/>.</summary>
public static class ResourcefullExceptionHandlerExtensions
{
    /// <summary>
    /// Registers <see cref="ResourcefullExceptionHandler"/>, and problem details, which it writes its responses with.
    /// Call <c>app.UseExceptionHandler()</c> too, so the handler runs.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddResourcefullExceptionHandler(this IServiceCollection services)
    {
        services.AddProblemDetails();
        return services.AddExceptionHandler<ResourcefullExceptionHandler>();
    }
}
