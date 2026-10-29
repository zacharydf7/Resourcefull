# Troubleshooting

## `InvalidOperationException: No provider configured.`

`AddResourcefull` needs exactly one provider: call `UseSqlServer`, `UseInMemory` or `UseProvider` inside it. Calling
two throws `Provider has already been configured.`

## `ResourceConflictException` on upload

The name already exists and the upload is create-only: versioning is off and `Overwrite` isn't set, or `CreateOnly`
is set. Set `Overwrite = true` to replace the resource, or turn on versioning to keep both.

## `ResourceContainerNotFoundException` on upload

The container doesn't exist. `GetResourceContainerClient` doesn't create or check it; use
`CreateResourceContainerIfNotExistsAsync` once, for example at startup or before the first upload.

## `ResourceContentLengthMismatchException`

The upload declared a `Headers.ContentLength` that differs from the bytes its stream supplied, and nothing was stored.
Usually the stream wasn't at the start (rewind it with `stream.Position = 0`), or a request body was cut short.

## An upload stored zero bytes

Uploads read from the stream's current position. A `MemoryStream` you've just written is positioned at its end, so
rewind it first. Otherwise the upload stores an empty version, which with `Overwrite` replaces the existing content.

## `ArgumentException` for a name that looks fine

Names can't start or end with whitespace, and are limited to 128 characters for a container and 256 for a resource. A
name read from a file or a form often carries a trailing newline or space. See [Names](containers-and-names.md#names).

## A resource "disappeared" after upgrading

Names are case-sensitive since 3.0. Look it up in the case it was stored in; `GetResourcesAsync` lists the stored
names.

## `InvalidOperationException`: "…its content can't be read again to retry"

A transient SQL Server failure interrupted an upload whose stream can't seek, such as an HTTP request body, so it
couldn't be retried. Nothing was stored; upload again. To have such failures retried automatically, upload from a
seekable stream, such as a buffered form file or a `FileStream`.

## `IOException` while reading a download

With SQL Server, the version was deleted while it was being read, so its content ended early. Read the current version
again.

## Scripts fail with `QUOTED_IDENTIFIER` errors, or carry on after an error

Run them with `sqlcmd -b -I`: `-I` turns on `QUOTED_IDENTIFIER`, which the filtered indexes need, and `-b` stops at
the first error. In SSMS, use SQLCMD mode with `:on error exit` at the top.
