# Changelog

## [Unreleased]

### Added

- **PostgreSQL.** The new `Resourcefull.PostgreSql` package stores resources in PostgreSQL, with everything the SQL
  Server provider does: chunked streaming, versions, conditional writes, copies and moves, expiry, staged uploads,
  container policies, export and import. `UsePostgreSql(connectionString, configure, schema)` registers it, and
  `MigrateResourcefullDatabaseAsync` and `GenerateResourcefullScript` provision it. It keeps times to the microsecond,
  lists names in Unicode code point order, and refuses U+0000 in the names, version IDs and content types it stores.
  `AddResourcefullPostgreSqlCheck` is its health check, and it counts retries and abandoned uploads in
  `resourcefull.postgresql.*`. It's tested against PostgreSQL 16 and 17. See [PostgreSQL](docs/providers/postgresql.md).
- **SQLite.** The new `Resourcefull.Sqlite` package stores resources in a SQLite database file, with no server to run,
  and does everything the other providers do: `UseSqlite(connectionString, configure)` registers it,
  `MigrateResourcefullDatabaseAsync` provisions it and turns on write-ahead logging, and `AddResourcefullSqliteCheck` is
  its health check. It's built on the same EF Core code as the SQL Server and PostgreSQL providers. Each write takes the
  database's write lock as it begins, so writers take turns. Times are stored as UTC ticks, which keeps their full
  precision. Like PostgreSQL, it lists names in Unicode code point order and refuses U+0000. It counts abandoned uploads
  in `resourcefull.sqlite.abandoned_uploads`. See [SQLite](docs/providers/sqlite.md).
- **File-system storage.** The new `Resourcefull.FileSystem` package stores resources as files in a directory, on a
  local disk or a network share, with no database or storage service: `UseFileSystem(rootPath)` registers it. It works
  as the S3 and Azure Blob providers do and shares their code, so it supports everything they do. Each write goes to a
  file flushed to disk, then moved into place, so nothing reads a file half-written; a change to a name holds a lock
  file while it compares and writes, so processes sharing the directory take turns. Names are escaped so that no
  filesystem changes them, which keeps names that differ only in case or Unicode normalization apart on Windows and
  macOS. They're listed in Unicode code point order. On macOS, a name whose file's path would pass the system's
  1,023-byte limit throws `ResourceNameException`. It counts its retries, recovered moves, stale expiry entries and
  abandoned uploads in `resourcefull.filesystem.*`, and is tested on Linux, Windows and macOS. See
  [File system](docs/providers/file-system.md).
- **S3-compatible storage.** The new `Resourcefull.S3` package stores resources in a bucket of Amazon S3 or another
  store that speaks its API, using only the store's conditional writes (`If-None-Match: *` and `If-Match` on
  `PutObject`) to keep one current version per name. `UseS3(bucketName, configure)` registers it, with a client and a
  key prefix of your choosing. It supports versions, create-only and conditional writes and deletes, copies, container
  and version metadata, container policies, export and import, expiry, upload sessions, and moving and renaming. A move
  marks both names, then commits with one conditional write of its entry, so its versions are never seen in both places
  or in neither; across containers, the store copies the content. Once a day, the expiry sweep deletes content no head
  names, which writes and deletes that stopped partway leave behind, and finishes purging deleted containers. Expiry
  keeps an index of the versions that expire, so a sweep lists only what's due. Content larger than 78 GiB, the most one
  multipart upload of 8 MiB parts takes, goes up in pieces that the store then joins, so an upload holds up to S3's 5
  TiB; past that, it throws the new `ResourceUploadLimitException` (413 Content Too Large in ASP.NET Core). An upload
  session stores its appends in segments of up to 64 MiB, and completing has the store join them with `UploadPartCopy`,
  so the bytes don't pass through the application. Once a session's segments take as many parts as the store joins, the
  next append first has the store join those since the last join into one, so a session holds up to 5 TiB too; an append
  past that throws the same exception. Names are kept exactly as given, even by stores that keep keys as files, and are
  listed in Unicode code point order. It counts writes retried after a concurrent change to the same name or session in
  `resourcefull.s3.retries`, stale expiry entries a sweep dropped in `resourcefull.s3.stale_expiries`, abandoned
  sessions and unnamed content the sweep deleted in `resourcefull.s3.abandoned_uploads`, joins of a session's segments
  in `resourcefull.s3.session_joins`, and, in `resourcefull.s3.moves_recovered`, moves the sweep finished or undid after
  their mover stopped. It's tested against SeaweedFS. See [S3-compatible storage](docs/providers/s3.md).
- **Azure Blob Storage.** The new `Resourcefull.AzureBlob` package stores resources in a blob container of an Azure
  Storage account, working as the S3 provider does and sharing its code: conditional writes (`If-None-Match: *` and
  `If-Match`) keep one current version per name, and content of 8 MiB or more goes up in blocks.
  `UseAzureBlobStorage(containerName, configure)` registers it, with a `BlobServiceClient` and a key prefix of your
  choosing, or the `BlobServiceClient` registered in your services. It supports everything the S3 provider does.
  Completing an upload session has Azure join its segments with `Put Block From URL`, reading them through a link signed
  with the account key or, with Microsoft Entra ID, a user delegation key. It counts writes retried after a concurrent
  change to the same name or session in `resourcefull.azureblob.retries`, stale expiry entries a sweep dropped in
  `resourcefull.azureblob.stale_expiries`, abandoned sessions and unnamed content the sweep deleted in
  `resourcefull.azureblob.abandoned_uploads`, joins of a session's segments in `resourcefull.azureblob.session_joins`,
  and, in `resourcefull.azureblob.moves_recovered`, moves the sweep finished or undid after their mover stopped. It's
  tested against Azurite. See [Azure Blob Storage](docs/providers/azure-blob.md).
- **Resumable uploads from browsers and apps.** `MapResourcefullUploads` maps endpoints that speak tus 1.0 over upload
  sessions, with the creation, creation-with-upload, termination and expiration extensions, so tus clients such as
  Uppy and tus-js-client resume large uploads with no upload code of their own. A required `ResolveAsync` decides
  each upload's container, name, mode, content type and metadata, or refuses it, and `OnCompletedAsync` runs once the
  file is stored. A client is told an upload finished only once its version is stored: a `Create` upload whose name
  exists is refused when it starts, a refused completion cancels the upload, and one that fails otherwise answers 503;
  when the client resumes, completing is tried again, and while it still fails the endpoints answer 423 Locked, which
  tus clients retry. Upload URLs are sealed with Data Protection and work only for the user who started them, and only
  with endpoints mapped with the same pattern for the same store (`StoreName` picks a named store). No request stores
  more than its upload has left, so the endpoints lift ASP.NET Core's request size limit for their own requests;
  `MaxSize` sets a limit of their own, and `WithResourcefullUploadHeaders` adds what they need to a CORS policy. Each
  request's span gets `resourcefull.tus`. The core gains `ResourcefullOptions.AbandonedUploadsDeletedAfter`, which the
  endpoints send as `Upload-Expires`. `MapResourcefullUploadLinks` maps the same endpoints for upload links, so
  whoever holds one can resume an upload without signing in: the link's name or prefix, size limit, content types and
  mode are checked when the upload starts, and its expiry stops new uploads, not those under way. See
  [Resumable uploads](docs/aspnet-core/resumable-uploads.md) and
  [Large files: resumable uploads](docs/upload-links.md#large-files-resumable-uploads).
- **Uploading in parts.** An upload session stores a large file over several appends, and what's stored survives a
  dropped connection or a restart: `StartUploadAsync`, then `AppendUploadAsync` from each part's offset,
  `GetUploadAsync` to find where to resume, and `CompleteUploadAsync`, which makes the version current with the
  write mode and conditions it's given, as `UploadAsync` does. `CancelUploadAsync` gives up on one. An append that
  doesn't start where the stored content ends throws `ResourceUploadOffsetException` (409 Conflict in ASP.NET
  Core), so a part is never stored twice. Completing reads the content back once to hash and inspect it. The expiry
  sweep deletes a session nothing was appended to for a day, and the same goes for what a stopped upload left behind,
  which used to be swept after an hour. Providers support sessions by overriding six new `ResourceRepository`
  members, and `Resourcefull.Testing` gains a `SupportsUploadSessions` switch for those that don't; every built-in
  provider supports them. A session that declared a length completes only with that length appended
  (`ResourceContentLengthMismatchException` otherwise), and the `Resourcefull_AddDeclaredLength` migration adds the
  nullable column that keeps it. Each call records a span, and appends count bytes in `resourcefull.client.appended`.
  See [Uploading in parts](docs/uploading-in-parts.md).
- **A conditions check for providers.** `ResourceConditions.ThrowIfNotMet(resource, currentVersionId,
  currentContentHash)` makes the check every provider must make when a write has conditions: given the current version
  the write read, it throws `ResourcePreconditionFailedException` if they aren't met. A provider can call it instead of
  writing its own check, and the built-in providers now do. `ResourceConditions.IsEmpty` is public too. See
  [Writing a provider](docs/providers/writing-a-provider.md#the-contract).
- **The contract tests as a package.** `Resourcefull.Testing` holds the tests every provider must pass, as an
  abstract xUnit v3 class (`xunit.v3` 4.0.2 or later): derive from `ResourcefullContractTests`, register your
  provider, and the suite runs against it. For each optional feature a provider leaves out (container metadata,
  changing a version's metadata and content type, moves, imports or expiry), its test class turns off the `Supports…`
  switch; that feature's tests are then skipped, and other tests check that the clients refuse it with
  `NotSupportedException`. `TimePrecision` and `NameOrder` describe a
  store that keeps times less precisely or orders names differently. See
  [Testing a provider](docs/providers/writing-a-provider.md#testing-a-provider).
- **Several stores in one application.** `AddKeyedResourcefull("archive", …)` adds a store under a name, beside the one
  `AddResourcefull` adds, with its own provider and settings; resolve its client with
  `[FromKeyedServices("archive")]`. Each store has its own policies, URI provider, upload inspector, cleanup handler
  and expiry sweep. `MigrateResourcefullDatabaseAsync`, `GenerateResourcefullScript`, the SQL Server hash backfill and
  both health checks take a store's name. Spans and metrics from a named store carry `resourcefull.store`. Providers
  read the store's name from `ResourcefullOptionsBuilder.StoreName`, and a new `UseProvider` overload takes a factory.
  `IResourceServiceClient.StoreName` says which store a client works with; a class that implements the interface
  implements it too. Signed links work with every store: `AddResourcefullLinks("archive")` adds a keyed signer for a
  named store, and `Results.SharedResource` and `Results.SharedUpload` check a token with the signer of the store whose
  client they're given. A link made for one store doesn't open at another's endpoint. See
  [Several stores](docs/several-stores.md).
- **Listing containers a page at a time.** `ListContainersAsync` enumerates every container, or those whose names start
  with a prefix, fetching a page at a time, so it holds only one page in memory, even in a store with a container for
  each user. `GetContainerPageAsync` returns one page and a continuation token, for an API that pages for its own
  callers. Providers implement `GetContainerPageAsync` in place of `GetAllResourceContainerInfoAsync`, which returned
  every container. Each page records a `list_containers` span with `resourcefull.page_size`. See
  [Listing containers](docs/containers.md#listing-containers).
- **A schema for SQL Server's tables.** `UseSqlServer(connectionString, configure, schema: "resourcefull")` puts
  Resourcefull's tables, and its own migration history, in a schema of their own, so they don't mix with an
  application's tables in `dbo`. `MigrateResourcefullDatabaseAsync` creates the schema, and the new
  `GenerateResourcefullScript` writes the provisioning script for it, since the packaged scripts are for `dbo`. Without
  a schema nothing changes. The `migrate` span records `resourcefull.schema`. See
  [Choosing a schema](docs/providers/sql-server.md#choosing-a-schema), which also shows how to move existing tables.
- **Exporting and importing containers.** `ExportContainerAsync` writes a container, every version of every file, to
  a zip, and `ImportContainerAsync` creates a new container from one, keeping each version's ID and `LastModified`.
  Imports check the manifest and every file's hash and length, and delete what they created if anything fails.
  Providers get `ImportVersionAsync`, which returns what it stored and whose default throws `NotSupportedException`.
  With SQL Server, a version whose write hits a transient failure is retried, reading its file from the export again.
  While an import runs, its container's metadata has `resourcefull.import: in-progress`, so one that never finished can
  be found; providers get `SetContainerMetadataAsync` for it, whose default leaves imports unmarked. Exports
  read a container 1,000 names at a time, through `GetVersionPageAsync`, and write the manifest to a temporary file
  rather than holding it in memory; providers can answer it in one query, and its default asks for each name's
  versions in turn. See
  [Backing up and moving containers](docs/backup-and-move.md).
- **Moving to another provider.** `CopyContainerToAsync` copies a container to another store, such as one with another
  provider, with every version as it was. It stores through the destination's new `ImportContainerAsync` overload,
  which imports from a stream of versions rather than a zip. `CopyStoreToAsync` copies every container. By default a
  container the destination already has fails; with `ExistingContainers.SkipMatching` it's skipped once it's found to
  match version for version, and with `ExistingContainers.Update` it's brought up to date in place through the new
  `ImportVersionsAsync`. `Update` never deletes the container. `StoreCopyFailures.Stop`, the default, stops at the
  first failure with a `StoreCopyException`; `Continue` reports every failure in the result. An import's mark now
  carries the time it was last written, in `resourcefull.import_at`, so a copy can tell an import still running from
  one that stopped. Copies record `copy_container` and `copy_store` spans and count versions in
  `resourcefull.client.copy.versions`; `ImportVersionsAsync` records an `import_versions` span. See
  [Moving to another provider](docs/moving-providers.md).
- **Signed, expiring links.** `Resourcefull.AspNetCore` can create links that let anyone download one file, without
  signing in, until they expire: `AddResourcefullLinks`, `IResourceLinkSigner.CreateTokenAsync` and
  `Results.SharedResource`. Links are sealed with ASP.NET Core Data Protection, can't be changed or read, and show the
  version that was current when they were made unless they follow the current one. See
  [Sharing files with links](docs/sharing-links.md).
- **Upload links.** `IResourceLinkSigner.CreateUploadToken` creates links that let anyone upload without signing in
  until they expire. A link uploads to one name or to names under a prefix, and can set a size limit, allowed content
  types, and whether an upload may replace a file (by default it can't). `Results.SharedUpload` serves them, checking
  the link's limits before the body is read, and `CheckUpload` reads a token. Upload and download tokens are sealed
  apart, so neither works as the other. `ResourceContainerPolicy.AllowsContentType` says whether a policy allows a
  type. A `POST` of a `multipart/form-data` form, as a plain HTML `<form>` sends one, stores the files in the form: a
  link with a prefix stores each under the prefix and its file name, and a link to one name takes one file. Each file
  is checked against the link's types and refused once it passes the link's size limit. The first file refused stops
  the upload, and the JSON response lists the files stored. See [Receiving files through links](docs/upload-links.md).
- **A dashboard.** `app.MapResourcefullDashboard("/resourcefull").RequireAuthorization("admins")` maps a
  page showing the health check's result, how many expired versions are waiting for the sweep, each container's usage
  against its policy, its largest versions, the versions that expire soonest, its files, and each file's versions,
  along with the JSON endpoints the page reads, which can serve as an admin API. It refuses to run until
  authorization is required on it, a fallback policy covers it, or it's marked `AllowAnonymous()`. For development,
  `ResourcefullDashboardOptions.AllowChanges` lets it upload, delete and restore files and versions, change a
  version's expiry, content type and metadata, and create and delete containers. `DownloadLinkPath` and
  `UploadLinkPath` let it create links. Each change needs a header that the dashboard's page sends and another site's
  page can't, and is logged with the user who made it. See [The dashboard](docs/aspnet-core/dashboard.md).
- **Versions by size or expiry.** `GetTopVersionsAsync(ResourceVersionOrder.LargestFirst)` lists a container's
  largest versions, current and older, to show what's filling it, and `ResourceVersionOrder.ExpiringFirst` the ones
  that expire soonest. `IResourceServiceClient.GetExpiredVersionsAsync` lists the expired versions the sweep hasn't
  deleted yet, in every container, to show whether it's keeping up. Providers answer the first through a new
  `ResourceRepository.GetTopVersionsAsync`, whose default reads every version; SQL Server and PostgreSQL answer it in
  one query. The two methods record `list_top` and `list_expired` operations.
- **Expiring resources.** `ResourceUploadOptions.ExpiresAt` sets when a version expires, and `UseExpiry` runs a
  background service that deletes expired versions within one interval, on every instance safely. `SetExpiryAsync`
  changes or clears a version's expiry, or the current version's with a `null` version ID. An
  `IResourceCleanupHandler` hears about every version Resourcefull deletes by itself,
  expired or pruned, before it's deleted, for auditing. With expiry on, pruning sets a version to expire five minutes
  later instead of deleting it, so a download already reading it finishes. The `AddExpiresAt` migration adds a
  nullable column and an index. Providers get three members whose defaults throw `NotSupportedException`, and
  `DeleteExpiredVersionsAsync`, which a sweep deletes each page of up to 100 expired versions through: with SQL
  Server, one transaction for the page (more when its versions hold over 256 chunks, about 10 MB, so no transaction
  logs a page of large files): 16 expired 1 MiB files take 14 round trips, where one version at a time took 98. Its
  default deletes them one at a time. See [Expiring resources](docs/expiry.md).
- **Retention dates and legal holds.** `ResourceUploadOptions.RetainUntil` keeps a version from being deleted until a
  date, and `LegalHold` until the hold is lifted; `SetRetentionAsync` moves a version's date later (never earlier), and
  `SetLegalHoldAsync` sets or lifts its hold. `ResourceContainerPolicy.RetainFor` gives every new version in a
  container a date. While either applies, a delete of the version, its name or its container throws
  `ResourceRetainedException` (409 with the ASP.NET Core exception handler) and deletes nothing (unless the version was
  protected while a batched delete was under way), expiry leaves it, and
  pruning keeps it past `MaxVersions`. Moves keep both, and exports and imports carry them. Every provider supports
  them, deciding in the delete itself, so a hold set at the same time can't lose the race; the
  `Resourcefull_AddRetention` migration adds two columns and two small indexes. Refused deletes are counted in
  `resourcefull.client.delete.refused`. Providers get `SupportsRetention`, `SetRetentionAsync` and
  `SetLegalHoldAsync`, and with one that doesn't support them, a write that would protect a version throws
  `NotSupportedException`. See [Deleting safely](docs/deleting-safely.md).
- **The bin.** `ResourceContainerPolicy.KeepDeletedFor` keeps deleted and pruned versions in their container's bin for
  that long. `UndeleteAsync` brings them back as older versions, and `ListDeletedAsync` and `GetDeletedPageAsync` list
  them, newest deletion first, with `ResourceInfo.DeletedAt` and `PurgeAt`. The expiry sweep
  purges each version at its `PurgeAt`, fixed when it was deleted, telling the cleanup handler first
  (`ResourceCleanupReason.Purged`), and `PurgeDeletedAsync` purges one early. Expired versions and container deletes
  skip the bin, and the bin doesn't count toward `MaxContainerSize` or `MaxResourceCount`: `ResourceContainerUsage`
  reports it separately, as `DeletedSize` and `DeletedVersionCount`, and so does the dashboard. `KeepDeletedFor` needs
  `UseExpiry`. Every provider has a bin; the `Resourcefull_AddBin` migration adds two columns and two small indexes.
  The bin records `list_deleted`, `undelete`, `purge_deleted` and `purge` spans and the `resourcefull.client.purged`,
  `purge.failures` and `undeleted` counters. Providers get `SupportsSoftDelete` and six members, and with one that
  doesn't have a bin, a delete in a container that keeps deleted versions throws `NotSupportedException`. See
  [Deleting safely](docs/deleting-safely.md#the-bin).
- **Finding resources by metadata.** `UseMetadataSearch("customer")` names the metadata keys a listing can search by,
  and `ResourceListOptions.Metadata = new ResourceMetadataMatch("customer", "42")` lists the resources whose current
  version has that value, in name order, by page, with a prefix if you like. Every provider supports it: SQL Server,
  PostgreSQL and SQLite keep the index in a `ResourceMetadata` table, which the `Resourcefull_AddMetadataSearch`
  migration adds, and the file system, S3 and Azure Blob keep an index entry for each value, so a search reads only the
  matches. A searchable key's value is limited to 256 characters. The container client's `IndexMetadataAsync` indexes
  versions stored before a key was named. It has an `index_metadata` span and counts what it indexed in
  `resourcefull.client.metadata.indexed`; a search's `list_page` span carries the key searched, never its value, and
  the file system, S3 and Azure Blob count the stale index entries a search dropped in
  `resourcefull.*.stale_search_entries`. S3's key prefix can be at most 190
  bytes of UTF-8, and Azure Blob's at most 190 characters, to leave room for the index entries. Providers get
  `FindResourcePageAsync`, whose default reads every name, and `IndexMetadataAsync`. See [Finding resources by
  metadata](docs/listing.md#finding-resources-by-metadata).
- **Copy, move and rename.** `CopyResourceAsync` copies a version to a name in the same or another container, as a
  new version there, and `MoveResourceAsync` moves a resource with every version, keeping their IDs; renaming is a
  move within the container. With SQL Server, no content passes through the application. A copy can be
  create-only or conditional, as an upload can. A copy or restore records
  its source in the new `ResourceInfo.CopiedFrom`. Providers get `CopyResourceAsync`, with a default implementation,
  and `MoveResourceAsync`, which throws `NotSupportedException` until implemented. The `AddCopiedFrom` migration adds
  three nullable columns. `RenameResourceAsync` renames within the container, and `IResourceContainerClient.Name`
  gives the container's name; both have default implementations. `RestoreVersionAsync` restores through
  `CopyResourceAsync`, so with SQL Server a restore
  runs within the database and is retried after a transient failure (for a version stored before 3.0, once the hash
  backfill has recorded its hash). See [Copying and moving](docs/copying-and-moving.md).
- **Container policies.** `UseContainerPolicies` sets limits for every container, and `UsePolicyProvider` gives any
  container its own, such as a quota for each user's container that an admin can change while the application runs:
  `MaxResourceSize`, `MaxContainerSize`, `MaxResourceCount`, `AllowedContentTypes` and `ReadOnly`. Sizes are
  `ResourceSize` values, such as `ResourceSize.Megabytes(100)`, or `"100 MB"` in configuration. A refused write throws
  `ResourcePolicyViolationException` and stores nothing, and `AddResourcefullExceptionHandler` turns it into 413, 409, 403
  or 415. When the policy provider fails, a container's last known policy stands in for up to 5 minutes
  (`keepLastKnownFor`). `GetUsageAsync` reports what a container holds, and `GetPolicyAsync` the limits its writes
  are checked against. Providers get `GetContainerUsageAsync` with a
  default implementation, so providers written for 3.1 keep working. See [Container policies](docs/container-policies.md).
- **Inspecting uploads.** An `IResourceUploadInspector`, registered with `UseUploadInspector`, reads each upload's
  and imported version's content while it's stored, so it can scan it for malware or check that it's the type it
  claims, and refuse it. No version becomes current until it answers; a refusal fails the upload with
  `ResourceContentRefusedException`, which `AddResourcefullExceptionHandler` turns into 422. The content is read
  once and never held whole, and with SQL Server nothing is locked while the inspector decides. Providers must read
  the content to its end before making a version current, which every built-in provider does. See
  [Inspecting uploads](docs/upload-inspection.md), with examples for ClamAV and Windows AMSI.
- **Changing metadata and content type.** `SetResourceMetadataAsync` and `SetContentTypeAsync` change a version's
  metadata or content type in place, without uploading it again: it keeps its ID, content, `LastModified` and ETag,
  and no new version is made. A new content type is checked against `AllowedContentTypes`, and a read-only container
  refuses both. `IResourceContainerClient.SetMetadataAsync` replaces a container's metadata, which
  could only be set when the container was created. Providers get `SetResourceMetadataAsync` and
  `SetContentTypeAsync`, whose defaults throw `NotSupportedException`. See
  [Changing metadata and content type](docs/uploading.md#changing-metadata-and-content-type).
- **Exceptions say what they're about.** `ResourcefullException` has `ContainerName` and `ResourceName`, set by
  Resourcefull's providers and filled in by the clients for a provider that doesn't. A container conflict has no
  `ResourceName`, so it can be told from a resource conflict without reading the message. `ResourceNameException`
  has `Name`, the name that broke the rules. Every message names the container or resource it's about, in one wording.
  See [Errors](docs/errors.md#what-an-exception-is-about).
- **Settings from configuration.** `UseConfiguration(section)` reads versioning, expiry, telemetry and container
  policy settings from configuration, such as appsettings.json. Each section present turns its feature on, and a key
  that isn't a setting stops the application at startup. See
  [Settings from configuration](docs/getting-started.md#settings-from-configuration).
- **Range downloads.** A `DownloadStreamingAsync` overload takes a `ResourceRange` and reads only those bytes from
  storage, for resuming downloads and seeking in media; every download's result now has `TotalLength`, and `Range`
  for a range. `Results.Resource(container, name)` in `Resourcefull.AspNetCore` looks the resource up itself, the
  current version or the `versionId` given, and answers `Range` requests with 206 Partial Content, honoring
  `If-Range`, and 416 for a range past the end. A range past the end throws `ResourceRangeNotSatisfiableException`,
  with the content's length, which `AddResourcefullExceptionHandler` answers with 416. Providers get a range overload
  of `GetResourceStreamAsync` with a
  default implementation, so providers written for 3.1 keep working.
  See [Downloading part of a resource](docs/downloading.md#downloading-part-of-a-resource).
- **Health checks.** `AddHealthChecks().AddResourcefullCheck()` reports whether storage can be reached through any
  provider, and `AddResourcefullSqlServerCheck()` also reports Unhealthy when the database is missing migrations this
  release needs, and Degraded when a newer release has upgraded it. See [Health checks](docs/health-checks.md).
- **Telemetry.** Every client operation records a trace span and metrics, through `ActivitySource` and `Meter`
  named `Resourcefull`: each operation's duration, and the bytes uploaded and downloaded, tagged by operation,
  provider and error type. SQL Server's migrations and hash backfill record spans. Resource names, and failure
  messages that may contain them, go on spans only with `UseTelemetry(t => t.RecordResourceNames = true)`. Counters
  for failed prunes, expired versions deleted and failed, cleanup handler failures, and SQL Server's retries show work
  that doesn't fail the call that started it. Nothing is measured until something subscribes. See
  [Telemetry](docs/telemetry.md).
- **Pruning a whole container.** The container client's `PruneAsync` prunes every resource in its container to
  `MaxVersions`, as each upload prunes its own name, for after an import or a failed prune. See
  [Versioning](docs/versioning.md#keeping-a-limited-number).
- **SQL Server options.** `UseSqlServer(connectionString, sql => ...)` changes EF Core's SQL Server options after
  Resourcefull's own, such as the command timeout or the retries. See [SQL Server](docs/providers/sql-server.md#retries).
- **Disposable downloads.** `ResourceDownloadResult` implements `IDisposable` and `IAsyncDisposable`, disposing its
  stream, so `await using` works on the download itself.
- **Razor Pages.** The results `TypedResults.Resource` and `TypedResults.SharedResource` return are also MVC action
  results, so a Razor Pages handler, which can't return an `IResult`, can return them. See
  [MVC, Razor Pages and Blazor](docs/aspnet-core/mvc-razor-blazor.md#razor-pages).

### Changed

- **One method per operation.** Each operation of `IResourceContainerClient` and `IResourceServiceClient` from before
  3.1 is now one method, with its optional parameters and then an optional `CancellationToken` last, rather than a pair
  of overloads where only one took a token. A token can now be named without passing `null` for the parameters
  before it: `UploadAsync("a", data, cancellationToken: token)`. `DownloadStreamingAsync`
  takes its range, and `DeleteResourceIfExistsAsync` its conditions, as optional parameters before the token, so a
  token passed by position after a version ID needs naming. `UploadAsync`'s `uploadOptions` parameter is now
  `options`. `GetResourcesAsync`, which read a whole container into memory, is removed, and with it the `list` span;
  use `ListResourcesAsync`. See [Upgrading to 4.0](docs/upgrading-to-4.md#one-method-per-operation).
- **Uploads say whether they replace.** `ResourceUploadOptions.Mode` and `ResourceCopyOptions.Mode`, a
  `ResourceWriteMode`, replace `Overwrite` and `CreateOnly`: `Create`, the default, refuses a name that exists, and
  `Replace` stores a new current version, whether or not versioning is on. With versioning on, an upload of an
  existing name is now refused unless it asks to replace. Conditions need `Replace`. `ResourceContainerPolicy` gains
  `MaxVersions`, so each container can keep its own number of versions; `UseVersioning` sets the default for the
  rest. `ResourcefullOptions.IsVersioningEnabled` is gone. `RestoreVersionAsync` works with versioning off, as long
  as the version exists. The `upload` and `copy` spans gain `resourcefull.write_mode`. `UploadFromRequestAsync` still
  replaces, as a `PUT` does. See [Upgrading to 4.0](docs/upgrading-to-4.md#uploads-say-whether-they-replace).
- **Provisioning works for any provider.** `MigrateResourcefullDatabaseAsync` moves from `Resourcefull.SqlServer` to
  `Resourcefull`, beside the new `GenerateResourcefullScript`, and a provider that keeps a database registers a
  `ResourceDatabase` for them to work on. For a store on the in-memory provider, migrating now does nothing rather than
  throwing. See [Upgrading to 4.0](docs/upgrading-to-4.md#provisioning-works-for-any-provider).
- **Adding Resourcefull twice throws.** A second `AddResourcefull` replaced the provider and kept the first call's other
  settings; it now throws `InvalidOperationException`. Add a second store with `AddKeyedResourcefull`. See
  [Upgrading to 4.0](docs/upgrading-to-4.md#adding-resourcefull-twice).
- **A container client holds only the container's name.** `IResourceContainerClient.Info` is gone, so changing it can
  no longer send the client's calls to another container, and `Name` is the container's name. `GetInfoAsync` reads the
  container's attributes as they're stored now (`Info` on a client from `GetContainerClient` had empty metadata). The
  operations on one existing container are on its client: `GetInfoAsync`, `SetMetadataAsync`, `PruneAsync` and
  `IndexMetadataAsync`, in place of the service client's `GetContainerInfoAsync` (`GetResourceContainerInfoAsync` in
  3.x), `SetContainerMetadataAsync`, `PruneContainerAsync` and `IndexMetadataAsync`. `ContainerInfo` is a sealed record
  with `init` properties and read-only metadata, like `ResourceInfo`, so changing its metadata can't look like it
  changed what's stored. `ResourceInfo` and `ContainerInfo` compare their metadata by its entries, so two reads of the
  same version, or of the same container, are equal. Telemetry's operation names don't change. See
  [Containers](docs/containers.md).
- **Consistent names.** The container methods drop "Resource": `CreateContainerAsync`,
  `CreateContainerIfNotExistsAsync`, `DeleteContainerIfExistsAsync` and `GetContainerClient`, beside the existing
  `ContainerExistsAsync`. `GetResourceContainersAsync`, which read every container into memory, is replaced by
  `ListContainersAsync`, which lists a page at a time. `ResourceContainerInfo` is `ContainerInfo`. `SetUriProvider` is
  `UseUriProvider`. `ResourceUploadOptions` has `init` properties. The provider types move to `Resourcefull.Providers`,
  and the provider's container members are renamed to match. Telemetry's operation names don't change. See
  [Upgrading to 4.0](docs/upgrading-to-4.md#consistent-names).
- **Stricter names.** Uploads, copies, moves and renames, and creating or importing a container, refuse new names with
  control characters, Unicode's bidirectional controls or unpaired surrogates, and `ResourceNames.IsValidContainerName`
  and `IsValidResourceName` now return `false` for them. Names already stored stay readable and deletable, resources
  with them can be renamed, and exports of them can be imported. See [Names](docs/names.md).
- **The stored `Uri` columns are dropped.** The `Resourcefull_DropStoredUri` migration drops `Uri` from `Resources`
  and `ResourceContainers`; `Uri` comes from your URI provider on every read, as it has since 3.0. 3.x applications
  still read and write the columns, so they fail against an upgraded database: stop them before migrating, and roll
  back by restoring a backup. See [Upgrading to 4.0](docs/upgrading-to-4.md#upgrading-the-database).
- **Providers derive from the `ResourceRepository` base class,** which replaces the `IResourceRepository` interface.
  Its members take a `ResourceLocation` rather than a container name and a resource name, return `IReadOnlyList<T>`
  rather than `List<T>`, and take metadata as `IReadOnlyDictionary<string, string>`. A write is given a
  `NewResourceVersion` rather than a `ResourceInfo`; it holds only what the write's caller can know. `AddResourceAsync`
  and `DeleteResourceAsync` each have one overload, taking an optional condition, so every provider decides
  conditional writes itself. Providers no longer store `Uri`. See [Upgrading to 4.0](docs/upgrading-to-4.md#custom-providers).
- **`ResourceInfo` can't be changed:** it's a sealed record with `init`-only properties, and its `Metadata` is an
  `IReadOnlyDictionary<string, string>`. Change a stored version with `SetResourceMetadataAsync` or
  `SetContentTypeAsync`, or make a changed copy with `with`. See [Upgrading to 4.0](docs/upgrading-to-4.md).
- **`IResourceContainerClient` isn't `IDisposable`,** since its `Dispose` did nothing; remove `using` where you call
  `GetContainerClient`.
- **Uploads to SQL Server hold nothing while their content arrives.** An upload stores its version staged, hidden
  from every read, then its content a MiB at a time, each batch committing on its own on a connection it gives back,
  and makes the version current in a short transaction at the end. So a slow client no longer holds a connection,
  locks and the transaction log for as long as it takes, and a transient failure partway sends the batch again rather
  than failing the upload, even from a stream that can't seek, such as a request body. A canceled or failed upload
  deletes what it stored; the expiry sweep deletes what an upload whose process died left behind, a day after it
  last stored anything. An upload whose staged version is deleted before it completes, as by a container delete at the
  same time, throws `ResourceUploadInterruptedException` (409 with the ASP.NET Core exception handler) and stores
  nothing. Each upload costs about one round trip more. The `Resourcefull_AddStagedAt` migration adds a nullable column
  and an index. Providers get `DeleteAbandonedUploadsAsync`, whose default does nothing. The upload
  span gains `resourcefull.batches`, and the `expire` span `resourcefull.abandoned_uploads`. See
  [Uploads, connections and the log](docs/providers/sql-server-operations.md#uploads-connections-and-the-log).
- **`UseVersioning` with fewer than one version** throws `ArgumentOutOfRangeException`, as `UseExpiry` and
  `UseContainerPolicies` do for a value out of range, rather than `InvalidOperationException`.
- **The core package has three new dependencies,** for features new in this release:
  `Microsoft.Extensions.Caching.Memory` (container policies' cache), `Microsoft.Extensions.Diagnostics.HealthChecks`
  (`AddResourcefullCheck`) and `Microsoft.Extensions.Hosting.Abstractions` (the `UseExpiry` sweep). ASP.NET Core
  applications already have them; others take them on upgrading.
- **Downloads are safer by default.** `Results.Resource` sends `Cache-Control: private, no-cache`, so shared caches
  such as CDNs no longer keep files (set `ResourceResultOptions.CacheControl` to cache public files), and
  `X-Content-Type-Options: nosniff`. `Inline` now displays only types that are safe to show (common image formats
  (PNG, JPEG, GIF, WebP, AVIF and BMP; not SVG), PDF, audio, video and plain text) and offers anything else as a
  download; `InlineContentTypes` changes the list. See [Serving downloads](docs/aspnet-core/serving-downloads.md).
- **Deleting a resource with many versions on SQL Server** happens in batches that each commit, oldest first and the
  current version last, rather than in one transaction, so it no longer holds the whole history in one transaction. A
  delete that's canceled or fails partway leaves the rest whole, and calling it again finishes it. Deleting a large
  version also no longer locks the whole content table while it runs. See
  [Versioning](docs/versioning.md#deleting).
- **A new look.** The packages have a new icon, and the [documentation site](https://zacharydf7.github.io/Resourcefull/)
  has a new design, a home page, and a sidebar grouped by what you're trying to do. Pages that covered several topics
  are split into one page each; links to the old pages still work.
- **`ResourceInfo.VersionId` is non-nullable,** since every stored version has one, so passing it to methods that
  take a version ID no longer needs `!`. Code that checked it for `null` still compiles. It's also `required`, so a
  provider must set it on every `ResourceInfo` it builds.
- **Deleting a large container on SQL Server is faster:** each batch deletes its versions in one round trip without
  loading them first. A container of a million files deletes in about a third less time, with about 40% fewer round
  trips and allocations.
- **SQL Server stores content in about 11% less space.** New content goes in 40,200-byte chunks, which fill SQL
  Server's pages exactly; the 65,535-byte chunks of earlier releases also took a page of pointers each. Content already
  stored keeps its chunks and stays readable. Uploads also allocate about half the memory they did. See
  [How SQL Server stores files](docs/providers/sql-server-storage.md).

### Fixed

- **Creating or deleting a container on SQL Server** no longer reports failure after succeeding. When the database
  committed but the connection failed before saying so, the automatic retry ran again and reported a conflict for a
  create, or a concurrency error for a delete; now it recognizes its own success, as uploads already did.
- **A file moved out of a container while the container was being deleted on SQL Server** could be deleted from its
  new container. The delete now removes only what's still in the container it's deleting.
- **A write on SQL Server whose wait for its name's write lock lost a deadlock or timed out** failed with "Could not
  lock the resource name for writing" instead of being retried, as any other deadlock is. It now raises error 50001,
  which the retry policy retries.
- **A download's file name leaves out control characters and the characters that change text direction,** which a
  resource name can hold. A right-to-left override (U+202E) could make a download of `invoice` + U+202E + `fdp.exe`
  show as `invoiceexe.pdf`, and a name with a line break failed with an error instead of downloading.
- **The library's own awaits no longer risk a deadlock when the caller blocks on a call.** A desktop app, or any other app
  with a synchronization context, could hang when it waited for a call with `.Result` or `.Wait()`, because the
  library's own awaits resumed on that context. They now resume on the thread pool.

## [3.1.0] - 2026-10-29

New features, all additive: code written for 3.0 keeps compiling, and custom providers keep working. The one thing to
check is a test that expects exactly `ArgumentException` for a bad name; it's now the subclass
`ResourceNameException`.

### Added

- **Conditional uploads and deletes.** `ResourceUploadOptions.Conditions` and a `DeleteResourceIfExistsAsync`
  overload take `ResourceConditions`, so a write goes ahead only if the name's current version still has the version
  ID or content hash you read. The write decides it, so of concurrent writers that read the same version exactly one
  succeeds; the rest throw the new `ResourcePreconditionFailedException` (HTTP 412). See
  [Updating only if unchanged](docs/concurrency.md#updating-only-if-unchanged).
- **`Resourcefull.AspNetCore`**, a new package for serving and accepting files. `Results.Resource(download)` sends a
  download with `Content-Length`, a strong `ETag`, `Last-Modified` and `Content-Disposition`, and answers conditional
  and `HEAD` requests. `UploadFromRequestAsync` stores a request body, honoring `If-Match`, `If-None-Match` and
  `If-Unmodified-Since` race-free. `AddResourcefullExceptionHandler` turns Resourcefull's exceptions into problem
  details with the right status codes. See [ASP.NET Core](docs/aspnet-core/index.md).
- **`ResourceNameException`**, thrown for a name that breaks the name rules. It derives from `ArgumentException`, so
  existing `catch (ArgumentException)` code still catches it, but a test that expects exactly `ArgumentException`
  needs updating.
- **Listing by prefix, a page at a time.** `ListResourcesAsync` enumerates a container's current resources, or those
  under a prefix, fetching pages as you go, and `GetResourcePageAsync` returns one page with a continuation token for
  APIs that page for their own callers. With SQL Server a page costs the same wherever it starts. See
  [Listing resources](docs/listing.md).
- **For provider authors:** `IResourceRepository.GetResourcePageAsync`, whose default implementation sorts and filters
  the full listing, so providers written for 3.0 keep working until they answer it directly.
- **For provider authors:** conditional overloads of `IResourceRepository.AddResourceAsync` and
  `DeleteResourceAsync`, with default implementations, so providers written for 3.0 keep compiling and refuse
  conditional writes with `NotSupportedException`.

### Fixed

- With SQL Server, a delete whose commit succeeded but whose acknowledgment was lost to a transient error, and was
  retried, reported that nothing was deleted. It now reports the delete.

## [3.0.0] - 2026-10-29

The first release of Resourcefull for general use. Coming from an earlier release, read
[Upgrading to 3.0](docs/upgrading-to-3.md) first: it lists every change to code, behavior and the database, with what each
needs from you. Items marked *(from 1.x)* were already in 2.x.

### Added

- **Content hashes.** Every version records the SHA-256 of its content, measured as it's written, as
  `ResourceInfo.ContentHash`. `ContentHashBackfill` records hashes for versions stored earlier.
- **Measured lengths.** `ContentLength` is measured from the stored content, and an upload whose declared length
  differs throws `ResourceContentLengthMismatchException` and stores nothing.
- **Cancellation.** Every method that contacts storage has an overload that takes a `CancellationToken`. *(from 1.x)*
- **Create-only uploads** with `ResourceUploadOptions.CreateOnly`, decided by the write, so concurrent ones can't both
  succeed.
- **Provisioning from code** with `MigrateResourcefullDatabaseAsync`, into any database the connection string names.
- **Writing a provider.** `IResourceRepository` is public, with a documented contract.
- **Name checks.** `ResourceNames.IsValidContainerName` and `IsValidResourceName` check a name against the rules
  before you use it.
- **Logging.** A version prune that fails after its upload was stored is logged as a warning.
- **Documentation** for every concept, both providers, ASP.NET Core and upgrading, and an API reference generated from
  the XML documentation that ships in the packages.

### Changed

- Methods return values and throw exceptions instead of returning `ErrorOr`, and several were renamed. *(from 1.x)*
- Names are case-sensitive and limited to 128 characters for a container and 256 for a resource, with no whitespace at
  either end.
- Deleting a name without a version ID deletes all its versions.
- New version IDs are GUIDs (older ones stay valid), to compare rather than parse, and storage decides which version is
  current and the order versions are listed in.
- Uploads read from the stream's current position, without rewinding it first.
- `LastModified` is when the version became current, by the storing server's clock.
- Download streams can't seek, and with SQL Server, a download whose version is deleted midway throws `IOException`.
- `Uri` comes from the URI provider whenever a container or version is returned, so a changed base URL applies to
  everything already stored.
- The SQL Server package's EF Core migration classes are internal.
- SQL Server provisioning scripts no longer name a database, and ship in the package's `scripts` folder rather than
  being added to consuming projects.

### Fixed

- Concurrent uploads of one name always leave exactly one current version.
- Uploads and downloads of any size use a fixed amount of memory, and uploads make about half as many round trips.
- An upload into a container deleted moments before reports the missing container. *(from 1.x)*
- Deleting a large container works in bounded batches, and can be resumed if it stops partway.

[Unreleased]: https://github.com/zacharydf7/Resourcefull/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/zacharydf7/Resourcefull/compare/v3.0.0...v3.1.0
[3.0.0]: https://github.com/zacharydf7/Resourcefull/releases/tag/v3.0.0
