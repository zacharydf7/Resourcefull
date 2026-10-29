namespace Resourcefull;

/// <summary>
/// The base type for errors surfaced by Resourcefull operations. Catch this to handle any
/// Resourcefull-specific failure regardless of its specific kind.
/// </summary>
public class ResourcefullException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ResourcefullException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ResourcefullException(string message, Exception innerException) : base(message, innerException) { }
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
}
