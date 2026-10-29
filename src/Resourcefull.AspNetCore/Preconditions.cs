using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Evaluates a request's preconditions against a resource's current version, in the order RFC 9110 section 13.2.2
/// sets: <c>If-Match</c>, else <c>If-Unmodified-Since</c>; then <c>If-None-Match</c>, else <c>If-Modified-Since</c>.
/// </summary>
internal static class Preconditions
{
    /// <summary>The entity tag of a version: its content hash, quoted; none for a version stored without one.</summary>
    public static EntityTagHeaderValue? EntityTagOf(ResourceInfo? resource) => EntityTagOf(resource?.ContentHash);

    /// <inheritdoc cref="EntityTagOf(ResourceInfo?)"/>
    public static EntityTagHeaderValue? EntityTagOf(string? contentHash) =>
        contentHash is null ? null : new EntityTagHeaderValue($"\"{contentHash}\"");

    /// <returns>200 to go ahead, 304 Not Modified, or 412 Precondition Failed.</returns>
    public static int Evaluate(HttpRequest request, ResourceInfo? current)
    {
        RequestHeaders headers = request.GetTypedHeaders();
        bool getOrHead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        EntityTagHeaderValue? entityTag = EntityTagOf(current);
        // HTTP dates have whole seconds.
        DateTimeOffset? lastModified = current is null ? null : current.LastModified.AddTicks(-(current.LastModified.Ticks % TimeSpan.TicksPerSecond));

        if (headers.IfMatch.Count > 0)
        {
            // Strong comparison: a weak tag never matches.
            if (current is null || !headers.IfMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any)
                || (entityTag is not null && tag.Compare(entityTag, useStrongComparison: true))))
                return StatusCodes.Status412PreconditionFailed;
        }
        else if (headers.IfUnmodifiedSince is { } unmodifiedSince && lastModified > unmodifiedSince)
            return StatusCodes.Status412PreconditionFailed;

        if (headers.IfNoneMatch.Count > 0)
        {
            if (current is not null && headers.IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any)
                || (entityTag is not null && tag.Compare(entityTag, useStrongComparison: false))))
                return getOrHead ? StatusCodes.Status304NotModified : StatusCodes.Status412PreconditionFailed;
        }
        else if (getOrHead && headers.IfModifiedSince is { } modifiedSince && lastModified <= modifiedSince)
            return StatusCodes.Status304NotModified;

        return StatusCodes.Status200OK;
    }
}
