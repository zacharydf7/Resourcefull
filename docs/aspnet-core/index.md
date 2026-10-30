# ASP.NET Core

The `Resourcefull.AspNetCore` package serves and accepts files with the status codes and headers HTTP clients
expect, and turns Resourcefull's exceptions into status codes:

```shell
dotnet add package Resourcefull.AspNetCore
```

The endpoints in this section assume Resourcefull is registered as in [Getting started](../getting-started.md), and leave out
authorization and validation to stay short. In an application, add `RequireAuthorization()` and check which
containers, names, file types and sizes each caller may use.

This section covers:

- [Serving downloads](serving-downloads.md): sending files with the right headers, answering conditional requests,
  and range requests for video players and resumable downloads.
- [Accepting uploads](accepting-uploads.md): streaming request bodies and form files into storage, with
  preconditions, and raising the request size limits for large files.
- [Turning exceptions into status codes](status-codes.md): one handler that answers 404, 409, 412 and the rest.
- [URIs for your API](../uri-providers.md): giving containers and resources the URLs your endpoints serve them at.
- [Sharing files with links](../sharing-links.md): links that let anyone download one file, without signing in,
  until they expire.
