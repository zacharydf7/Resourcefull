# Errors

Resourcefull reports an expected absence with a return value and everything else with an exception.

| Situation | Result |
|---|---|
| A single resource, version or container that doesn't exist | `null` |
| A listing with nothing in it | an empty list, never `null` |
| An existence check, or a delete of something that may not exist (`…IfExistsAsync`) | `bool` |
| A `null` name, or a missing argument | `ArgumentNullException`, before storage is contacted |
| A name that breaks the [name rules](names.md) | `ResourceNameException`, an `ArgumentException`, before storage is contacted |
| Uploading a name that exists, when the upload is create-only | `ResourceConflictException` |
| Creating a container that exists | `ResourceConflictException` |
| Uploading into a container that doesn't exist | `ResourceContainerNotFoundException` |
| An upload or delete whose `ResourceConditions` the current version doesn't meet, including a delete with conditions of a name that doesn't exist | `ResourcePreconditionFailedException` (nothing changes) |
| Moving a resource onto a name that exists, or importing into a container that exists <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `ResourceConflictException` |
| A download range that starts past the end of the content <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `ArgumentOutOfRangeException` |
| A version deleted while it's being downloaded, with SQL Server | `IOException` from the download stream, rather than a stream that ends early |
| Importing a file that isn't an export, describes something that can't be imported, or whose content was changed <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `InvalidDataException` (nothing is left behind) |
| Importing from a stream that can't seek <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `ArgumentException` |
| An import that fails, and then fails to delete what it imported <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `AggregateException`, holding both failures |
| A feature the storage provider doesn't support, such as a provider written for an earlier release | `NotSupportedException` |
| An upload whose declared length differs from its content | `ResourceContentLengthMismatchException` (nothing is stored) |
| A write that breaks its container's [policy](container-policies.md) <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `ResourcePolicyViolationException` (nothing changes) |
| Restoring a version while versioning is off | `InvalidOperationException` |
| Configuring no provider, or two | `InvalidOperationException` from `AddResourcefull` |
| A canceled operation | `OperationCanceledException` |
| A storage failure | the provider's exception, such as `SqlException`; with SQL Server, a transient failure that outlasts the retries throws EF Core's `RetryLimitExceededException`, with the last failure as its inner exception |

## Reading from a container that doesn't exist

Writing into a container that doesn't exist throws `ResourceContainerNotFoundException`, but reading from it and
deleting from it treat it as an empty container: listings are empty, `GetUsageAsync` <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> reports
zero, downloads and `GetResourceInfoAsync` return `null`, and `ExistsAsync` and `…IfExistsAsync` deletes return
`false`. So a misspelled container name looks like an empty container rather than an error. Where that
matters, such as a container name that comes from a request, check the service client's `ContainerExistsAsync`
first and answer 404 if it's `false`:

```csharp
if (!await resources.ContainerExistsAsync(name, cancellationToken)) return Results.NotFound();
IResourceContainerClient container = resources.GetResourceContainerClient(name);
```

## Resourcefull's exceptions

All of them derive from `ResourcefullException`, so one `catch` handles any of them:

| Exception | Meaning | HTTP status it usually maps to |
|---|---|---|
| `ResourceConflictException` | The name or container already exists. | 409 Conflict |
| `ResourceContainerNotFoundException` | The container was never created, or was deleted before the write. | 404 Not Found |
| `ResourceContentLengthMismatchException` | The upload's declared length didn't match its content. | 400 Bad Request |
| `ResourcePreconditionFailedException` | The name's current version didn't meet the upload's or delete's conditions. | 412 Precondition Failed |
| `ResourcePolicyViolationException` <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | The write broke its container's policy; `Rule` says which rule, and `Limit` its limit. | 413 for the size limits, 409 for `MaxResourceCount`, 403 for `ReadOnly`, 415 for `AllowedContentTypes` |

`ResourceNameException` derives from `ArgumentException` rather than `ResourcefullException`, so existing
`catch (ArgumentException)` code still catches it; it maps to 400 Bad Request.

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

The [ASP.NET Core package](aspnet-core/status-codes.md) maps them all, the name exception
included, to responses in one place.
