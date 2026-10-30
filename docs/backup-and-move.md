# Backing up and moving containers

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

You can write a whole container, every file and every version, to a single zip file, and turn that file back into a
container later, in the same store or another one. Use it to:

- **keep a copy** of one container before a risky change (to back up the whole store, see
  [Backups and recovery](backups-and-recovery.md));
- **move** a container between environments, such as from production to a staging copy;
- **hand over** a customer's files, or keep a copy when you close their account.

```csharp
// Export: write the container to a file.
await using (FileStream file = File.Create("invoices.zip"))
    await resources.ExportContainerAsync("invoices", file, cancellationToken);

// Import: create a new container from the file.
await using (FileStream file = File.OpenRead("invoices.zip"))
    await resources.ImportContainerAsync("invoices-restored", file, cancellationToken);
```

Both methods are on `IResourceServiceClient`, and work with every built-in provider.

## What you get back

An import restores the container as it was:

- **Every version of every file,** not only the current ones, in the same order, so the same version is current.
- **Each version's ID and `LastModified`.** Imported under the container's original name, anything that recorded a
  version ID still finds it: [signed links](sharing-links.md), audit records, links in your own database. Imported under
  another name, the IDs are the same, but links that name the old container don't follow.
- **Everything else about each version:** content type, metadata, content hash, [`CopiedFrom`](copying-and-moving.md)
  and [`ExpiresAt`](expiry.md).
- **The container's metadata.**

Both methods return what they did: how many resources and versions, and how much content.

## What's in the file

The export is an ordinary zip, so you can open it with any zip tool to see what's inside:

- **`manifest.json`** describes the container and every version: its ID, when it was stored, its type, metadata and
  content hash, and which file in the zip holds its content.
- **`content/`** holds the files' contents, each once. Versions with the same content, such as a file uploaded twice
  or a copy, share one entry, so copies don't make the export bigger.

The manifest records its format, and Resourcefull's tests import an export exactly as 3.2 writes it, so a later
release can't change the format by accident.

## Exporting

An export reads one version at a time, so a file's size doesn't change how much memory it uses; only the number of
versions does, because the manifest describing them is written at the end. It writes to any stream, including one that
can only be written forward, such as an HTTP response. That lets an admin download a container
directly:

```csharp
app.MapGet("/admin/containers/{name}/export", async (string name, IResourceServiceClient resources,
    HttpResponse response, CancellationToken cancellationToken) =>
{
    // Checked first: once the zip starts streaming, the response can't turn into a 404.
    if (!await resources.ContainerExistsAsync(name, cancellationToken)) return Results.NotFound();

    response.ContentType = "application/zip";
    response.Headers.ContentDisposition = $"attachment; filename=\"{name}.zip\"";
    await resources.ExportContainerAsync(name, response.Body, cancellationToken);
    return Results.Empty;
}).RequireAuthorization("admins");
```

The application keeps working while a container is exported. Files uploaded or deleted during an export may or may not
be in it, as with any backup taken while the application runs.

## Importing

An import always creates a **new** container: it refuses a name that already exists (`ResourceConflictException`). To
replace a container with a backup, either delete the container first, or import under another name and point your
application at it once you've checked it.

It needs a stream it can **seek**, such as a file, because a zip keeps its table of contents at the end. To import a file
someone uploads, save the upload to a temporary file first:

```csharp
app.MapPost("/admin/containers/{name}/import", async (string name, HttpRequest request,
    IResourceServiceClient resources, CancellationToken cancellationToken) =>
{
    await using var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 81920, FileOptions.DeleteOnClose);
    await request.Body.CopyToAsync(file, cancellationToken);
    file.Position = 0;

    ResourceImportResult imported = await resources.ImportContainerAsync(name, file, cancellationToken);
    return Results.Ok(imported);
}).RequireAuthorization("admins");
```

### Imports are checked, and all or nothing

- **The manifest is checked first.** Names that break the [name rules](names.md), repeated
  names or version IDs, and missing fields are refused (`InvalidDataException`) before anything is created.
- **Every file is checked against its hash and length** as it's imported, so content damaged in storage or on the way
  is refused (`InvalidDataException`) rather than restored wrong. A file longer than the length the manifest gives it
  is refused before more than that is stored.
- **A failed import deletes what it created.** If anything goes wrong, such as a refused file or a canceled import, the
  import deletes the container again. Fix the cause and import again. If the delete fails too, as it can when the
  database is unreachable or out of space, you get an `AggregateException` holding both errors, and should delete the
  container yourself before trying again; the same goes if the application stops partway through an import.
- **The new container's [policies](container-policies.md) apply,** as to any upload: a size limit, file-type list or
  read-only setting for the container's name refuses an import that breaks it.

### Only import exports you trust

The hashes in an export catch accidental damage, not deliberate edits: whoever can change a file's content can change
its hash in `manifest.json` too. An export is as trustworthy as where it came from, so only import files your
application wrote, or that came from someone you trust, and keep backups where only you can change them.

An export is also compressed, so a small file can describe far more content than its own size: many versions of one
highly compressible file, for example. An import writes what its manifest declares, so to bound what any import can
write, give containers a `MaxContainerSize` with [container policies](container-policies.md); an import is checked
against it like any upload.

### Things to know

- **Versions over `MaxVersions` are kept.** An import restores the whole history it was given, even if the store keeps
  fewer versions of each file. The extra versions are [pruned](versioning.md#keeping-a-limited-number) the next time each
  file is uploaded, or all at once with `PruneContainerAsync`:

  ```csharp
  await resources.ImportContainerAsync("restored", file);
  await resources.PruneContainerAsync("restored");
  ```
- **Expired versions come back too.** A version whose expiry has passed is restored, then deleted by the next
  [expiry](expiry.md) sweep.
- **Names stay the same, but the container's name is yours to choose,** so the same export can be imported several
  times under different names.

## Custom providers

Export only reads, so it works with any provider. Import stores each version with its original ID and time, which
needs the provider method `ImportVersionAsync`; a provider written before it existed throws `NotSupportedException`.
See [Writing a provider](providers/newer-members.md#importing-versions).

## Telemetry

Each export and import records an `export` or `import` operation, with `resourcefull.resources` and
`resourcefull.versions` counting what it moved. See [Telemetry](telemetry.md).

## Troubleshooting

**`ResourceConflictException` when importing.** The container already exists. Import under another name, or delete it
first.

**`InvalidDataException`: "This isn't a container export".** The file isn't a zip, or it's a zip without a
`manifest.json`, so it wasn't written by `ExportContainerAsync`.

**`InvalidDataException`: "doesn't match its hash".** The file was damaged in transit or storage, or edited after it was
exported. Use another copy of the backup.

**`InvalidDataException`: "names a resource … twice" or "can't be imported".** The manifest was edited, or written by
something other than `ExportContainerAsync`. Use the original export.

**`AggregateException`: "deleting what it had imported failed too".** The import failed and couldn't clean up after
itself. Delete the container named in the message, fix whatever stopped the import (often the database), and import
again.

**`ArgumentException`: "needs a stream that can seek".** You passed a stream that can only be read forward, such as a
request body. Copy it to a temporary file first, as in [Importing](#importing).

**`NotSupportedException` when importing.** The storage provider doesn't support imports; see
[Custom providers](#custom-providers).
