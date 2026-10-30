# Turning exceptions into status codes

Register the handler once, rather than catching exceptions in every endpoint:

```csharp
builder.Services.AddResourcefullExceptionHandler();

app.UseExceptionHandler();
```

| Exception | Status |
|---|---|
| `ResourceConflictException` | 409 Conflict |
| `ResourceContainerNotFoundException` | 404 Not Found |
| `ResourceContentLengthMismatchException` | 400 Bad Request |
| `ResourceNameException`, for a name that breaks the [name rules](../names.md) | 400 Bad Request |
| `ResourcePreconditionFailedException` | 412 Precondition Failed |
| `ResourcePolicyViolationException` <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, for a write that breaks its [container's policy](../container-policies.md) | 413 Content Too Large for the size limits, 409 Conflict for `MaxResourceCount`, 403 Forbidden for `ReadOnly`, 415 Unsupported Media Type for `AllowedContentTypes` |

The responses are [problem details](https://www.rfc-editor.org/rfc/rfc9457) whose title is the exception's message.
Resourcefull's messages name the resource or container involved, and sometimes the argument or option, but nothing
about your infrastructure, so they're safe to return. Other exceptions, such as a `SqlException`, are left to the default handler, which returns 500 without
details in production. ASP.NET Core removes `ETag` from responses its exception handling writes, so a 412 from the
handler doesn't carry the current version's ETag; the 412 that `UploadFromRequestAsync` returns does.
