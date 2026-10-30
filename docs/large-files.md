# Large files

Uploads and downloads both stream, so a file's size doesn't change how much memory they use. A few things still
matter with large files:

- **Pass streams, not byte arrays.** `BinaryData` and `DownloadContentAsync` hold the whole file in memory.
- **A failed upload can be retried only if its stream can seek.** With SQL Server, an upload is one transaction. If a
  transient failure interrupts it, Resourcefull rewinds a seekable stream (a `FileStream`, a `MemoryStream`, the
  `BinaryData` overload) and tries again. A stream that can't seek, such as an HTTP request body, fails instead; the
  caller can upload again. Nothing is stored either way.
- **Web servers limit request sizes.** See [large uploads in ASP.NET Core](aspnet-core/accepting-uploads.md#large-uploads).
