using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Extensions.Primitives;
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

    /// <summary>Whether the request has any of the preconditions <see cref="Evaluate"/> considers.</summary>
    public static bool Any(HttpRequest request)
    {
        RequestHeaders headers = request.GetTypedHeaders();
        return headers.IfMatch.Count > 0 || headers.IfNoneMatch.Count > 0
            || headers.IfModifiedSince is not null || headers.IfUnmodifiedSince is not null;
    }

    /// <summary>
    /// Records how the request's preconditions came out, as <c>resourcefull.precondition</c> (<c>met</c>,
    /// <c>not_modified</c> or <c>failed</c>), on the request's own span, when there is one, rather than on a span the
    /// application may have started around the call.
    /// </summary>
    public static void Record(HttpContext httpContext, string outcome) =>
        httpContext.Features.Get<IHttpActivityFeature>()?.Activity.SetTag("resourcefull.precondition", outcome);

    /// <returns>200 to go ahead, 304 Not Modified, or 412 Precondition Failed.</returns>
    public static int Evaluate(HttpRequest request, ResourceInfo? current)
    {
        RequestHeaders headers = request.GetTypedHeaders();
        bool getOrHead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        EntityTagHeaderValue? entityTag = EntityTagOf(current);
        DateTimeOffset? lastModified = current is null ? null : HttpDate(current.LastModified);

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

    /// <summary>
    /// Whether a range request's <c>If-Range</c>, when it has one, holds for the current version: its entity tag
    /// matches strongly, or its date is exactly the last change. When it doesn't, or can't be parsed, the request gets
    /// the whole content.
    /// </summary>
    public static bool RangeHolds(HttpRequest request, ResourceInfo current)
    {
        if (StringValues.IsNullOrEmpty(request.Headers.IfRange)) return true;
        return request.GetTypedHeaders().IfRange switch
        {
            { EntityTag: { } tag } => EntityTagOf(current) is { } entityTag && tag.Compare(entityTag, useStrongComparison: true),
            { LastModified: { } date } => date == HttpDate(current.LastModified),
            _ => false
        };
    }

    /// <summary>A time as an HTTP date carries it, in whole seconds.</summary>
    private static DateTimeOffset HttpDate(DateTimeOffset time) => time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond));
}
