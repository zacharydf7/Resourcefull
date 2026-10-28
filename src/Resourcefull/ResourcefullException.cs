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
public sealed class ResourceConflictException(string message) : ResourcefullException(message)
{
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
