# Uploading in parts

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

A large file over a slow or unreliable connection is hard to send in one go: when the connection drops two
gigabytes in, an ordinary upload has to start again from the beginning. An upload session sends the file in parts
instead. What has arrived stays stored, so after a dropped connection, or even a restart of the server, the client
asks how much is stored and carries on from there. Nothing shows the new version until the session is completed,
and then it becomes the name's current version all at once, as an upload's does.

Every built-in provider supports sessions: SQL Server, PostgreSQL, [S3](providers/s3.md),
[Azure Blob](providers/azure-blob.md) and in-memory.

## A session from start to finish

```csharp
// 1. Start: say what the file is. Nothing is stored yet.
ResourceUpload upload = await container.StartUploadAsync("video.mp4", new ResourceUploadSessionOptions
{
    Headers = new ResourceHeaders("video/mp4", ContentLength: fileLength),
    Metadata = new Dictionary<string, string> { ["camera"] = "front door" }
});

// 2. Append parts, each from where the last one ended.
foreach (Stream part in parts)
    upload = (await container.AppendUploadAsync("video.mp4", upload.UploadId, upload.Offset, part))!;

// 3. Complete: the version becomes current.
ResourceInfo? stored = await container.CompleteUploadAsync("video.mp4", upload.UploadId);
```

- **`StartUploadAsync`** takes the version's content type, metadata and expiry, and the length the file is expected
  to have. It returns a `ResourceUpload` with the session's `UploadId`, which every later call names, and its
  `Offset`: how many bytes are stored, 0 at the start. Keep the upload ID somewhere that survives a restart, such as
  your own database or the client that's uploading.
- **`AppendUploadAsync`** stores a part. It takes the offset the part starts at, which must be the session's
  current `Offset`, and returns the session with its new offset. A part can be any size, from a few bytes to many
  gigabytes, and parts don't have to be the same size.
- **`CompleteUploadAsync`** makes the version current and returns it, as `UploadAsync` would have. The version's ID
  is the session's upload ID.
- **`CancelUploadAsync`** gives up on a session and deletes what it stored.

## Resuming after a failure

An append stores its part as it arrives, about a MiB at a time with SQL Server and PostgreSQL and up to 64 MiB at a
time with S3 and Azure Blob, so an append that fails partway keeps what it stored. (The in-memory provider stores an
append whole or not at all.) To resume, ask the session where it ended, and send the rest from there:

```csharp
ResourceUpload? current = await container.GetUploadAsync("video.mp4", uploadId);
if (current is null)
{
    // The session was completed, canceled, or deleted after a day without appends: start again.
}
else
{
    fileStream.Position = current.Offset;
    await container.AppendUploadAsync("video.mp4", uploadId, current.Offset, fileStream);
}
```

An append whose offset isn't where the stored content ends throws `ResourceUploadOffsetException`, and stores
nothing. Its `CurrentOffset` says where to resume. That's what happens when an append did store its part but its
answer never reached you, and you send the same part again: the second copy is refused, so the content is never
stored twice.

## Completing

Completing reads the stored content once from the start, to measure its length, compute its hash and pass it to
your [upload inspector](upload-inspection.md), if you have one. A session can't hash its content as the parts
arrive, because the parts can come from different processes, and a hash's running state can't be handed from one to
the next. So completing a large file takes about as long as reading it back: a few seconds per gigabyte from a
local database.

The write's mode and conditions go to `CompleteUploadAsync`, because that's when they're decided:

```csharp
ResourceInfo? stored = await container.CompleteUploadAsync("video.mp4", uploadId, ResourceWriteMode.Replace,
    new ResourceConditions { IfMatchVersionId = versionTheUserSaw });
```

- **`ResourceWriteMode.Create`, the default,** refuses a name that already has a current version, with
  `ResourceConflictException`. `StartUploadAsync` doesn't check this, and another upload can create the name while
  the session is open, so a `Create` completion can still be refused.
- **`ResourceWriteMode.Replace`** makes the session's version current over the existing one, and older versions are
  [pruned](versioning.md) as after any upload.
- **[Conditions](concurrency.md#updating-only-if-unchanged)** are checked against the name's current version as the
  session's version becomes current. They need `ResourceWriteMode.Replace`, as for an upload; with `Create`,
  `CompleteUploadAsync` throws `ArgumentException`.

When the mode, the conditions, the expected length or the container's policy refuses the completion, the session
stays as it was, so you can complete it again, for example with `Replace`, or after appending the rest. These checks
come before the content is read, so a completion that's refused costs little. When the upload inspector refuses the
content, the session is deleted, since that content is never to be stored.

## Limits and cleanup

- **[Container policies](container-policies.md)** apply as for an upload. `StartUploadAsync` checks that the
  container isn't read-only, that it allows the content type, and that the expected length fits. An append that
  would go past `MaxResourceSize`, or past `MaxContainerSize`, throws `ResourcePolicyViolationException`; part of
  what arrived before the limit may stay stored. Completing checks the container's size and count limits with the
  real length.
- **A session's content doesn't count toward the container's usage** until the session completes, so it doesn't take
  room from other uploads meanwhile, and its appends are held only to the container's limits as a whole, not to the
  room that's left. Limit who can start sessions as you limit who can upload.
- **The expected length,** when you give `StartUploadAsync` one, is checked against the container's size limits at
  the start, and again as the session completes: completing a session whose appended content is a different length
  throws `ResourceContentLengthMismatchException`, so a client that completes too early doesn't store a cut-off file.
  `ResourceUpload.DeclaredLength` returns this length, so a client that resumes knows when it's done.
- **A session nothing is appended to for 24 hours is deleted** by the [expiry](expiry.md) sweep, along with what it
  stored. Without `UseExpiry`, nothing sweeps sessions: call `DeleteExpiredAsync` on a schedule, or cancel the
  sessions you abandon, or they stay.
- **A session isn't moved or copied** with its name: moving or renaming the resource leaves a session for the old
  name where it is.
- **With S3 and Azure Blob, a session holds a limited number of segments,** the pieces of up to 64 MiB it stores: enough
  for at least 78 GiB with S3 and 390 GiB with Azure Blob, and more with appends of 64 MiB or larger. An append past the
  limit throws `ResourceUploadLimitException`; what the session stored stays, but it takes no more content. Appends
  smaller than 8 MiB each cost more there too, since each reads back the short piece the last one stored. See the
  providers' pages for the details.

## What each call returns when there's no session

`GetUploadAsync`, `AppendUploadAsync` and `CompleteUploadAsync` return `null`, and `CancelUploadAsync` returns
`false`, when there's no session with that ID for that name: it was completed, canceled or swept, or never existed.

## Telemetry

Each call records a span: `resourcefull upload_start`, `upload_get`, `upload_append` (with `resourcefull.offset`),
`upload_complete` and `upload_cancel`. Appends add to `resourcefull.client.appended`, the bytes stored by appends,
and a completion adds the version's length to `resourcefull.client.uploaded`, as an upload does. See
[Telemetry](telemetry.md).
