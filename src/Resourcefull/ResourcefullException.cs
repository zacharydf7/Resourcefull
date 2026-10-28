namespace Resourcefull;

/// <summary>
/// The base type for errors surfaced by Resourcefull operations. Catch this to handle any
/// Resourcefull-specific failure regardless of its specific kind.
/// </summary>
public class ResourcefullException : Exception
{
    public ResourcefullException(string message) : base(message) { }

    public ResourcefullException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when an operation would conflict with an existing resource or container — for example
/// uploading a resource whose name already exists when overwrite is not enabled, or creating a
/// container that already exists. Maps naturally to an HTTP 409.
/// </summary>
public sealed class ResourceConflictException : ResourcefullException
{
    public ResourceConflictException(string message) : base(message) { }

    public ResourceConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when an upload declares a content length that differs from the bytes it supplies. Nothing is
/// stored. Maps naturally to an HTTP 400.
/// </summary>
public sealed class ResourceContentLengthMismatchException : ResourcefullException
{
    public ResourceContentLengthMismatchException(string message) : base(message) { }
}

/// <summary>
/// Thrown when an operation targets a resource container that does not exist — for example
/// uploading a resource into a container that was never created or has since been deleted.
/// Maps naturally to an HTTP 404.
/// </summary>
public sealed class ResourceContainerNotFoundException : ResourcefullException
{
    public ResourceContainerNotFoundException(string message) : base(message) { }

    public ResourceContainerNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
