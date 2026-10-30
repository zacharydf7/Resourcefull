# Cancellation

Every method that contacts storage has an overload that takes a `CancellationToken` last. A canceled operation
throws `OperationCanceledException`; it never returns `null`, an empty list or a result for work it didn't finish.

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
ResourceInfo stored = await container.UploadAsync("backup.zip", file, null, timeout.Token);
```

- **A canceled upload stores nothing.** Once the new version is stored, though, the upload has succeeded, and the
  token no longer applies. Pruning older versions then runs to completion (see [Versioning](versioning.md)).
- **`DownloadStreamingAsync`'s token covers finding the resource.** To cancel reading the content, pass a token to
  the stream's `ReadAsync` or `CopyToAsync`. `DownloadContentAsync` does that for you.
- **Deleting a container can stop partway**, between whole versions; see
  [Containers](containers.md).
