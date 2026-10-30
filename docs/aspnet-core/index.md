# ASP.NET Core

The `Resourcefull.AspNetCore` package serves and accepts files with the status codes and headers HTTP clients
expect, and turns Resourcefull's exceptions into status codes:

```shell
dotnet add package Resourcefull.AspNetCore
```

The endpoints in this section assume Resourcefull is registered as in [Getting started](../getting-started.md), and leave out
authorization and validation to stay short. Before you put any of them on the internet, read
[Securing file endpoints](securing-endpoints.md): it shows how to decide who may read and write which files.

This section covers:

- [Securing file endpoints](securing-endpoints.md): signing people in, keeping each user to their own files, choosing
  file names, limiting uploads, and keeping links out of logs.
- [Serving downloads](serving-downloads.md): sending files with the right headers, answering conditional requests,
  and range requests for video players and resumable downloads.
- [Accepting uploads](accepting-uploads.md): streaming request bodies and form files into storage, with
  preconditions, and raising the request size limits for large files.
- [Turning exceptions into status codes](status-codes.md): one handler that answers 404, 409, 412 and the rest.
- [URIs for your API](../uri-providers.md): giving containers and resources the URLs your endpoints serve them at.
- [Sharing files with links](../sharing-links.md): links that let anyone download one file, without signing in,
  until they expire.
