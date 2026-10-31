namespace Resourcefull;

/// <summary>
/// The base type for errors surfaced by Resourcefull operations. Catch this to handle any
/// Resourcefull-specific failure regardless of its specific kind.
/// </summary>
public class ResourcefullException : Exception
{
    private string? _containerName;
    private string? _resourceName;

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourcefullException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ResourcefullException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// The container the error is about: for a copy or move, the destination when that's where the problem is, and
    /// for a <see cref="ResourcePolicyViolationException"/>, the container whose policy refused the write.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public string? ContainerName { get => _containerName; init => _containerName = value; }

    /// <summary>
    /// The resource the error is about, or <see langword="null"/> when it's about a container, as a
    /// <see cref="ResourceConflictException"/> for a container that exists is.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public string? ResourceName { get => _resourceName; init => _resourceName = value; }

    /// <summary>
    /// Records the container and resource of the operation that failed, unless whoever threw the exception already
    /// said what it's about, so an exception from a provider that doesn't set them still has them.
    /// </summary>
    internal virtual void LocateIfUnknown(string? containerName, string? resourceName)
    {
        if (_containerName is not null || _resourceName is not null) return;
        _containerName = containerName;
        _resourceName = resourceName;
    }
}

/// <summary>
/// Thrown when an operation would conflict with an existing resource or container — for example
/// uploading a resource whose name already exists when overwrite is not enabled, or creating a
/// container that already exists. Maps naturally to an HTTP 409.
/// </summary>
public sealed class ResourceConflictException : ResourcefullException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourceConflictException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ResourceConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a container or resource name breaks the name rules: empty, only whitespace, too long, or with whitespace
/// at either end. A <see langword="null"/> name throws <see cref="ArgumentNullException"/> instead. It derives from
/// <see cref="ArgumentException"/>, so code that catches that still catches it, and its
/// <see cref="ArgumentException.ParamName"/> names the argument. Maps naturally to an HTTP 400.
/// </summary>
public sealed class ResourceNameException : ArgumentException
{
    /// <summary>Creates the exception with a message and the name of the argument that broke the rules.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="paramName">The name of the argument that holds the name.</param>
    public ResourceNameException(string message, string? paramName) : base(message, paramName) { }

    /// <summary>Creates the exception with a message, the argument that broke the rules, and its value.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="paramName">The name of the argument that holds the name.</param>
    /// <param name="name">The name that broke the rules.</param>
    /// <remarks>Coming in 3.2.</remarks>
    public ResourceNameException(string message, string? paramName, string? name) : base(message, paramName)
    {
        Name = name;
    }

    /// <summary>
    /// The name that broke the rules, or <see langword="null"/> when whoever threw the exception didn't say.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public string? Name { get; }
}

/// <summary>
/// Thrown when a range download starts past the end of the content. Nothing is read. Maps naturally to an HTTP 416,
/// with <see cref="ContentLength"/> in its <c>Content-Range</c> header.
/// <para>Coming in 3.2.</para>
/// </summary>
public sealed class ResourceRangeNotSatisfiableException : ResourcefullException
{
    /// <summary>Creates the exception with a message and the content's length.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="contentLength">How many bytes the content has.</param>
    public ResourceRangeNotSatisfiableException(string message, long contentLength) : base(message)
    {
        ContentLength = contentLength;
    }

    /// <summary>How many bytes the content has, so a range within it can be asked for instead.</summary>
    public long ContentLength { get; }
}

/// <summary>
/// Thrown when an upload or delete with <see cref="ResourceConditions"/> finds that the name's current version doesn't
/// meet them. Nothing is changed. Maps naturally to an HTTP 412.
/// </summary>
public sealed class ResourcePreconditionFailedException : ResourcefullException
{
    /// <summary>Creates the exception with a message and what the name's current version was when it was checked.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="currentVersionId">The current version's ID, or <see langword="null"/> when the name had none.</param>
    /// <param name="currentContentHash">The current version's content hash, or <see langword="null"/> when it had none.</param>
    public ResourcePreconditionFailedException(string message, string? currentVersionId, string? currentContentHash)
        : base(message)
    {
        CurrentVersionId = currentVersionId;
        CurrentContentHash = currentContentHash;
    }

    /// <summary>The name's current version ID when the conditions were checked; <see langword="null"/> when it had none.</summary>
    public string? CurrentVersionId { get; }

    /// <summary>The current version's content hash when the conditions were checked, if it had one.</summary>
    public string? CurrentContentHash { get; }
}

/// <summary>
/// Thrown when an upload declares a content length that differs from the bytes it supplies. Nothing is
/// stored. Maps naturally to an HTTP 400.
/// </summary>
public sealed class ResourceContentLengthMismatchException : ResourcefullException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourceContentLengthMismatchException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    /// <remarks>Coming in 3.2.</remarks>
    public ResourceContentLengthMismatchException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when an operation targets a resource container that does not exist — for example
/// uploading a resource into a container that was never created or has since been deleted.
/// Maps naturally to an HTTP 404.
/// </summary>
public sealed class ResourceContainerNotFoundException : ResourcefullException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourceContainerNotFoundException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ResourceContainerNotFoundException(string message, Exception innerException) : base(message, innerException) { }

    // It's about the container, so it never names a resource, whichever operation it came from.
    internal override void LocateIfUnknown(string? containerName, string? resourceName) =>
        base.LocateIfUnknown(containerName, null);
}

/// <summary>
/// Thrown when a write breaks its container's <see cref="ResourceContainerPolicy"/>. Nothing is changed. Maps naturally
/// to an HTTP 413 for content too large for the limits, 409 for a container that holds as many resources as it may,
/// 403 for a read-only container, and 415 for a content type that isn't allowed.
/// <para>Coming in 3.2.</para>
/// </summary>
public sealed class ResourcePolicyViolationException : ResourcefullException
{
    /// <summary>Creates the exception with a message, the rule that was broken, and its limit.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="rule">The rule the write broke.</param>
    /// <param name="limit">The rule's limit, for the size and count rules.</param>
    public ResourcePolicyViolationException(string message, ResourcePolicyRule rule, long? limit = null) : base(message)
    {
        Rule = rule;
        Limit = limit;
    }

    /// <summary>The rule the write broke.</summary>
    public ResourcePolicyRule Rule { get; }

    /// <summary>
    /// The rule's limit: bytes for <see cref="ResourcePolicyRule.MaxResourceSize"/> and
    /// <see cref="ResourcePolicyRule.MaxContainerSize"/>, a number of resources for
    /// <see cref="ResourcePolicyRule.MaxResourceCount"/>, and <see langword="null"/> for the other rules.
    /// </summary>
    public long? Limit { get; }
}
