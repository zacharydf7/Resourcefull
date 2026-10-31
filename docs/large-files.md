# Large files

Uploads and downloads both stream, so a file's size doesn't change how much memory they use. A few things still
matter with large files:

- **Pass streams, not byte arrays.** `BinaryData` and `DownloadContentAsync` hold the whole file in memory.
- **A transient failure doesn't fail an upload,** with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>. The content goes in about a MiB at a time,
  each batch committed on its own, and a batch that fails is sent again from what it holds, so even a stream that
  can't seek, such as an HTTP request body, carries on. An upload that fails for good, or is canceled, stores
  nothing.
- **A slow upload holds nothing up,** with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>. No transaction, lock or connection is held while its
  content arrives; see [Uploads, connections and the log](providers/sql-server-operations.md#uploads-connections-and-the-log).
- **Web servers limit request sizes.** See [large uploads in ASP.NET Core](aspnet-core/accepting-uploads.md#large-uploads).
