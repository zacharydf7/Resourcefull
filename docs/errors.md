# Errors

Resourcefull reports an expected absence with a return value and everything else with an exception.

| Situation | Result |
|---|---|
| A single resource, version or container that doesn't exist | `null` |
| A listing with nothing in it | an empty list, never `null` |
| An existence check or a conditional delete | `bool` |
| A `null` or invalid name, or a missing argument | `ArgumentNullException` or `ArgumentException`, before storage is contacted |
| Uploading a name that exists, when the upload is create-only | `ResourceConflictException` |
| Creating a container that exists | `ResourceConflictException` |
| Uploading into a container that doesn't exist | `ResourceContainerNotFoundException` |
| An upload or delete whose `ResourceConditions` the current version doesn't meet | `ResourcePreconditionFailedException` (nothing changes) |
| An upload whose declared length differs from its content | `ResourceContentLengthMismatchException` (nothing is stored) |
| Restoring a version while versioning is off | `InvalidOperationException` |
| Configuring no provider, or two | `InvalidOperationException` from `AddResourcefull` |
| A cancelled operation | `OperationCanceledException` |
| A storage failure | the provider's exception, such as `SqlException`; with SQL Server, a transient failure that outlasts the retries throws EF Core's `RetryLimitExceededException`, with the last failure as its inner exception |

## Resourcefull's exceptions

All of them derive from `ResourcefullException`, so one `catch` handles any of them:

| Exception | Meaning | HTTP status it usually maps to |
|---|---|---|
| `ResourceConflictException` | The name or container already exists. | 409 Conflict |
| `ResourceContainerNotFoundException` | The container was never created, or was deleted before the write. | 404 Not Found |
| `ResourceContentLengthMismatchException` | The upload's declared length didn't match its content. | 400 Bad Request |
| `ResourcePreconditionFailedException` | The name's current version didn't meet the upload's or delete's conditions. | 412 Precondition Failed |

```csharp
try
{
    await container.UploadAsync(name, content, new ResourceUploadOptions { CreateOnly = true });
}
catch (ResourceConflictException)
{
    // someone else stored this name first
}
catch (ResourceContainerNotFoundException)
{
    // create the container, or report it missing
}
```

The [ASP.NET Core recipes](recipes/aspnet-core.md#turning-exceptions-into-status-codes) map them to responses in one
place.
