# Finding content stored twice

Resourcefull stores each version's content on its own, even when another version holds the same bytes. That's simple and
safe, but it means some content can be stored more than once:

- **Copies.** `CopyResourceAsync` stores a second copy of the content.
- **Restores.** `RestoreVersionAsync` stores the old version's content again as a new version.
- **Uploads of an unchanged file.** An application that uploads the same file again stores it again.

Every version records the SHA-256 hash of its content in `ResourceInfo.ContentHash`, so versions with the same hash hold
the same bytes. This page shows how to measure how much of a store is duplicated, and how to avoid a common cause.

## Measuring with any provider

This loop works with every provider. It reads each name's versions, so it takes a while on a large container. Run it from
a maintenance job, not on a request:

```csharp
var seen = new HashSet<string>();
long storedBytes = 0, duplicateBytes = 0;
await foreach (ResourceInfo resource in container.ListResourcesAsync(cancellationToken: cancellationToken))
{
    foreach (ResourceInfo version in await container.GetResourceVersionsAsync(resource.Name, cancellationToken))
    {
        if (version is not { ContentHash: { } hash, ContentLength: { } length }) continue;
        storedBytes += length;
        if (!seen.Add(hash)) duplicateBytes += length;
    }
}
Console.WriteLine($"{duplicateBytes:N0} of {storedBytes:N0} bytes in {container.Name} are copies of content it already holds.");
```

A version stored before 3.0 has no hash until SQL Server's
[hash backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions) reaches it. The loop skips versions
without a hash, and so does the query below (`ContentHash IS NOT NULL`), so neither total includes them.

## Measuring with SQL Server, PostgreSQL or SQLite

With a database provider, one query measures every container at once, without reading any content:

```sql
SELECT "ContainerName",
       SUM("Copies" - 1) AS "DuplicateVersions",
       SUM(("Copies" - 1) * "ContentLength") AS "DuplicateBytes",
       SUM("Copies" * "ContentLength") AS "StoredBytes"
FROM (SELECT "ContainerName", "ContentHash", MAX("ContentLength") AS "ContentLength", COUNT(*) AS "Copies"
      FROM "Resources"
      WHERE "StagedAt" IS NULL AND "ContentHash" IS NOT NULL
      GROUP BY "ContainerName", "ContentHash") AS "Distinct"
GROUP BY "ContainerName"
ORDER BY "DuplicateBytes" DESC;
```

It runs as written on all three. If you gave the provider a [schema](providers/sql-server.md#choosing-a-schema), put it
before the table's name, such as `"resourcefull"."Resources"`. The query reads every version's row, so on a large table
run it when the database is quiet. `StagedAt IS NULL` leaves out uploads still in progress.

`DuplicateBytes` is what storing each piece of content once per container would save. Content repeated across different
containers isn't counted: each container's content is kept and deleted along with that container, so two containers
couldn't share one copy.

## Not storing an unchanged file again

A common duplicate is a file uploaded again unchanged, such as by a nightly export or a sync job. Compare
the file's hash with the current version's before uploading:

```csharp
using var sha256 = SHA256.Create();
string hash = "sha256:" + Convert.ToHexStringLower(await sha256.ComputeHashAsync(file, cancellationToken));
file.Position = 0;

ResourceInfo? current = await container.GetResourceInfoAsync(name, cancellationToken: cancellationToken);
if (current?.ContentHash != hash)
{
    await container.UploadAsync(name, file, new ResourceUploadOptions { Mode = ResourceWriteMode.Replace },
        cancellationToken);
}
```

`hash` has the same form as `ContentHash`, `sha256:` followed by the digest in lowercase hex, so the two compare
directly. If another upload could replace the file between the check and the upload, add
`Conditions = new ResourceConditions { IfMatchVersionId = current?.VersionId }` to the options. The upload then
replaces only the version you compared with; if that version is no longer current, it throws
`ResourcePreconditionFailedException` and stores nothing. See [Concurrency](concurrency.md).
