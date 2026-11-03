# Azure Blob Storage

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

`Resourcefull.AzureBlob` stores resources in a blob container of an Azure Storage account. Files stream in and out
with flat memory use, and every name keeps its versions, content hashes and metadata, as with the database providers.
It's tested on every change against [Azurite](https://github.com/Azure/Azurite), Microsoft's local emulator for Azure
Storage.

```shell
dotnet add package Resourcefull.AzureBlob
```

```csharp
using Azure.Storage.Blobs;
using Resourcefull;
using Resourcefull.AzureBlob;

builder.Services.AddResourcefull(options => options.UseAzureBlobStorage("files", azure =>
    azure.Client = new BlobServiceClient(builder.Configuration.GetConnectionString("Storage"))));
```

The blob container, `files` here, must exist; Resourcefull doesn't create it. Every Resourcefull container is kept
inside it, so Resourcefull's container names aren't bound by Azure's rules for naming blob containers.

## What it supports

Uploads, downloads (with ranges), listing, versions, create-only and conditional writes, conditional deletes, copies,
container and version metadata, [container policies](../container-policies.md),
[export and import](../backup-and-move.md), and [expiry](../expiry.md). It passes the same contract tests as the other
providers, apart from those for two features it doesn't support yet:

- **Moving and renaming** throw `NotSupportedException`. A move must take every version at once, never leaving them
  in both places or in neither, and Blob Storage can't do that without transactions. Copy, then delete, instead.
  Planned in [#154](https://github.com/zacharydf7/Resourcefull/issues/154).
- **[Upload sessions](../uploading-in-parts.md)** throw `NotSupportedException`. A single upload of any size works.
  Planned in [#151](https://github.com/zacharydf7/Resourcefull/issues/151).

Every provider is meant to support every feature, so that switching between them takes only the line that picks the
provider; these two are the next work planned for this provider.

It works the same way as the [S3-compatible provider](s3.md), and shares its code: only the calls to the store differ.

## Connecting to an account

Give `UseAzureBlobStorage` a `BlobServiceClient` for the account. Any way of creating one works:

- **A connection string**, as above. For Azurite on your own machine, it's `UseDevelopmentStorage=true`.
- **Microsoft Entra ID**, with no secret to store, using the `Azure.Identity` package:

  ```csharp
  using Azure.Identity;

  options.UseAzureBlobStorage("files", azure => azure.Client =
      new BlobServiceClient(new Uri("https://myaccount.blob.core.windows.net"), new DefaultAzureCredential()));
  ```

- **A client registered in your application's services**, such as one Aspire or `Microsoft.Extensions.Azure`
  registers. Leave `Client` unset, and Resourcefull uses the registered `BlobServiceClient`. If there's none either,
  the first use of Resourcefull throws `InvalidOperationException`, saying to set one.

Keep connection strings and keys out of source control, in user secrets, environment variables or Key Vault.

## Permissions

Resourcefull only reads, writes, lists and deletes blobs: it never creates or deletes blob containers, or changes the
account's settings. With Microsoft Entra ID, the narrowest built-in role that allows that is **Storage Blob Data
Contributor**; give it to the application on the blob container rather than the whole account.

## Sharing a blob container

Every blob Resourcefull writes has a name starting with `resourcefull/`, so the blob container can hold other things
too. To keep several stores in one blob container, such as one per environment, give each a prefix of its own:

```csharp
options.UseAzureBlobStorage("files", azure =>
{
    azure.Client = client;
    azure.KeyPrefix = "staging/resourcefull/";
});
```

Don't let anything else write, change or delete blobs under Resourcefull's prefix, and give every instance of the
application the same blob container and prefix. A prefix can be up to 200 characters long; an empty one puts
Resourcefull's blobs at the top of the blob container.

## Account settings

- **Blob versioning** isn't needed, and is better left off for the account: Resourcefull rewrites a small blob for
  every change to a name, and with versioning on, Azure keeps every old copy of it, and bills for them.
- **Soft delete** keeps deleted blobs, and with versioning off the old copy of every overwritten one, for the number
  of days you choose, so they still cost storage after Resourcefull deletes or rewrites them. Since every change to a
  name rewrites its head, that adds up on busy names. It's a safety net if you want one; just count it in the bill.
- **Hierarchical namespace** (Data Lake Storage) isn't supported: it treats `/` in blob names as folders, which
  changes the order blobs are listed in.

## How it stores things

You don't need this to use the provider, but it explains its costs and what you'll see in the blob container.

- **Each container has a record**, `{prefix}c/{name}`, holding a random ID and the container's metadata. Everything
  the container holds is kept under that ID, at `{prefix}d/{id}/`.
- **Each name has a head**, a small JSON blob listing its versions' attributes in the order they were stored; the
  last is current. Every change to a name (an upload, a delete or a metadata change) reads the head and writes it back
  only if nobody changed it meanwhile (`If-Match` with the ETag it read, or `If-None-Match: *` for a new name). When
  somebody did, it reads it again and retries. Each write carries a random ID, so when an answer is lost and Azure
  refuses the resent write, Resourcefull can tell its own write landed.
- **Each version that expires has an entry** in an index under `{prefix}x/`, keyed by when it expires, so the
  [expiry](../expiry.md) sweep lists only what's due rather than reading every head. The entry goes in just before the
  head write that sets the expiry, so a version never has an expiry the index doesn't know about. Each entry's key
  holds a random token that the version records, so no two writes share a key. Changing an expiry writes a new entry,
  then deletes the old one once the head no longer names it; deleting a version deletes its entry with its content. An
  entry those deletes miss, such as one a failed write left, is dropped by a sweep that finds it due and over an hour
  old; a younger one may belong to a write still in progress.
- **Each version's content** is a blob of its own, uploaded before the head names it, and deleted after the head
  stops naming it.

In blob names, the space, the punctuation from `!` to `/`, `[` and `\` are escaped, and a `!` marks where each name
ends, so a name such as `../report.pdf` or `a//b` is kept exactly as given. The escaping keeps the names' order, so
listings come straight from the order Azure lists blobs in. Against Azurite, that's .NET's ordinal order, which the
contract tests check. Azure documents only that it lists blobs alphabetically, uppercase letters first, so names with
characters beyond U+FFFF, such as emoji, or from U+E000 to U+FFFF, might list in a different order there.

### What stays behind

- A deleted container keeps its record, marked deleted, and a name whose versions are all deleted keeps an empty head.
  Both are a few bytes each, and are reused if the name is created again.
- Deleting a container hides everything in it at once, then deletes its blobs a thousand at a time. If that stops
  partway, deleting the container again finishes it, and so does creating a container of that name again. Its
  versions' expiry entries are kept apart from it, under `{prefix}x/`, so they stay until they're due, and a sweep
  then drops them.
- A write interrupted after its content was uploaded, such as by a crash, leaves that content unnamed. It still counts
  toward the container's stored size until the container is deleted.
- An upload of 8 MiB or more that fails partway leaves the blocks it sent uncommitted. Azure discards uncommitted
  blocks on its own after a week.

## Costs

- **An upload** smaller than 8 MiB reads the container's record, sends the content in one request, reads and writes
  the head, then reads the container's record again to catch a container deleted meanwhile. A create-only or
  conditional upload, which includes the default mode, also reads the head before sending the content. From 8 MiB
  up, content goes up in 8 MiB blocks, each read into a buffer first, so each upload in progress holds 8 MiB of memory.
- **A download** reads the container's record and the head, then streams the content; a range asks Azure for only
  that range.
- **A listing page** reads the container's record, lists the heads, then reads each one, several at a time, since the
  attributes are inside them.
- **Usage**, which [container policies](../container-policies.md) check before each write they limit, comes from
  listings alone: the content blobs' sizes, and the number of non-empty heads. That's one list request per five
  thousand blobs in the container.
- **An expiry sweep** with nothing due is one list request. Each expired version it finds costs three reads to
  find it (its index entry, its container's record and its head), two more to delete it (the container's record and
  the head again), a write of the head, and two deletes, of its content and its entry. A version with an expiry costs
  one more small blob, and an upload with an expiry one more write, for its entry. Changing an expiry reads the
  container's record and the head, writes the new entry (none when clearing it), reads and writes the head, then
  deletes the old entry.

## Health checks

The [generic health check](../health-checks.md), `AddResourcefullCheck()`, makes one read through the provider, so it
reports whether the account can be reached and the blob container exists and is readable with the credentials given.

## Telemetry

Besides the operations every provider records (see [Telemetry](../telemetry.md)), the provider counts
`resourcefull.azureblob.retries`: writes to a name's head retried because another writer changed the name first. A few
are normal when writers share names; a steady rise means many writers are contending for the same names. It also counts
`resourcefull.azureblob.stale_expiries`: expiry entries a sweep dropped because their version no longer existed or
had another expiry. The Azure SDK can also trace its own requests; see its documentation for OpenTelemetry.
