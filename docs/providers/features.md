# Provider features

Your code talks to Resourcefull's clients, not to a database or a cloud store, so changing where the bytes go means
changing the one line that picks the provider. That only holds if every provider does the same things, so every
provider aims for every feature. This page shows, in one place, where each one stands.

## What each provider supports

| Feature | SQL Server | PostgreSQL | SQLite | File system | S3 | Azure Blob | In-memory |
|---|---|---|---|---|---|---|---|
| Uploads, downloads and listings | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Downloading a range](../downloading.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Versions](../versioning.md) and pruning | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Create-only and [conditional](../concurrency.md) writes and deletes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Copying](../copying-and-moving.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Moving and renaming](../copying-and-moving.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Container metadata | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Changing a version's metadata or content type | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Expiry](../expiry.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Retention and legal holds](../deleting-safely.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [The bin](../deleting-safely.md#the-bin) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Finding resources by metadata](../listing.md#finding-resources-by-metadata) | Indexed | Indexed | Indexed | Indexed | Indexed | Indexed | By reading every name |
| [Uploading in parts](../uploading-in-parts.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Export and import](../backup-and-move.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Container policies](../container-policies.md) | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| [Health check](../health-checks.md) | Its own | Its own | Its own | The generic one | The generic one | The generic one | The generic one |

Every provider passes the same [contract tests](writing-a-provider.md#testing-a-provider). What the clients do on top of
a provider works the same with all of them: [upload inspection](../upload-inspection.md),
[shared links](../sharing-links.md), the [ASP.NET Core endpoints](../aspnet-core/index.md) and the
[dashboard](../aspnet-core/dashboard.md). SQL Server, PostgreSQL and SQLite each have a health check of their own,
which also checks that the database has every migration the release needs; the generic check makes one read through the
provider.

A feature a provider doesn't support yet would show here as planned, with its issue, as the next work for that provider;
there are none today. A provider gives up a feature only after careful thought, for a real limitation of its store, and
then its page explains why and what to do instead. No provider gives one up today.

## Differences that aren't gaps

Some behavior follows the store underneath, though every provider meets the same contract:

- **The order of names in a listing.** SQL Server and the in-memory provider list names in .NET's ordinal order;
  PostgreSQL, SQLite, the file system and S3 in Unicode code point order. The two orders differ only for characters
  beyond U+FFFF, such as emoji, and those from U+E000 to U+FFFF. SQL Server also sorts a name followed by a character
  below U+0020 before the name itself. Azure Blob lists in whatever order Azure returns: .NET's ordinal order against
  Azurite, though Azure itself might order emoji and characters from U+E000 to U+FFFF differently. Every listing returns
  every name exactly once. See [Listing resources](../listing.md).
- **How precisely times are kept.** PostgreSQL keeps times to the microsecond, so a more precise expiry or imported
  version time is cut to the microsecond. The others keep .NET's full precision, a tenth of a microsecond.
- **The null character.** PostgreSQL and SQLite can't store U+0000, so they refuse a name, version ID, content type or
  origin that contains one, which an older release's export might carry. No provider accepts one in a new name, since
  the name rules refuse control characters.
- **The largest file.** SQL Server, PostgreSQL and SQLite store a file in chunks, and the file system as one file, with
  no limit of their own. S3 takes a single upload of up to 5 TiB, the most it keeps in one object, and Azure Blob one of
  up to about 48 TiB. A file larger than 78 GiB on S3 or 390 GiB on Azure, the most one multipart upload or block blob
  takes in 8 MiB parts, goes up in pieces of that size, which the store then joins. An
  [upload session](../uploading-in-parts.md) holds as much as one upload. The in-memory provider holds what memory
  allows.
- **The longest name.** On macOS, the file-system provider can't store a name of more than about 150 characters beyond
  ASCII, since the whole path to its file must fit in 1,023 bytes; see [File system](file-system.md#names).
- **What a call costs.** Each provider's page says what its calls cost in requests or queries, and what it leaves
  behind when something fails partway.

## Adding a provider

A new built-in provider ships with an issue for each feature it doesn't support yet, so the table above stays true.
[Writing a provider](writing-a-provider.md) shows how to write one of your own, and which features its contract tests
can skip.
