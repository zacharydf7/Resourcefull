# Listing resources

Enumerate `ListResourcesAsync` to list the current version of every resource in a container, or of those whose names
start with a prefix. It fetches a page at a time as you go, so it suits containers of any size:

```csharp
await foreach (ResourceInfo invoice in invoices.ListResourcesAsync(new ResourceListOptions { Prefix = "2026/10/" }))
{
    Console.WriteLine(invoice.Name);
}
```

For a small container, `await invoices.ListResourcesAsync().ToListAsync()` gives you them all in one list.

An API that pages for its own callers fetches one page per request and hands back the continuation token:

```csharp
ResourcePage page = await invoices.GetResourcePageAsync(
    new ResourceListOptions { Prefix = prefix, PageSize = 100 }, continuationToken, cancellationToken);
return Results.Ok(new { page.Resources, page.ContinuationToken });
```

- **Order:** by name, in ordinal order (`StringComparer.Ordinal`). Uppercase letters come before lowercase ones, and
  `2026/10` before `2026/10/a`. Some providers differ for a few names. SQL Server compares names as if the shorter were
  padded with spaces, so a name followed by a character below U+0020 sorts before the name itself: `a` followed by a tab
  and `b` comes before `a`. PostgreSQL orders names by Unicode code point, which lists a character beyond U+FFFF, such
  as an emoji, after those from U+E000 to U+FFFF rather than before; see [PostgreSQL](providers/postgresql.md). SQLite,
  the file system and S3 list in code point order too, and Azure Blob in whatever order Azure returns; see
  [Provider features](providers/features.md#differences-that-arent-gaps). In every case, a listing still returns every
  name exactly once; only the order of such names differs from the in-memory provider.
- **The prefix** is matched ordinally and case-sensitively, with every character taken literally, including `%`, `_`
  and `[`. Containers don't nest, so `2026/10/` is just the start of a name.
- **Page size:** from 1 to 5,000; 1,000 by default.
- **No snapshot.** Each page is a query of its own. A name that exists for the whole listing appears exactly once,
  and one created or deleted during it may or may not, so a listing never holds up uploads.
- **Continuation tokens** are opaque, URL-safe strings of at most about 1 KB, and they don't expire. Continue with the
  same prefix; the page size can change from page to page. A token from a listing of another prefix throws
  `ArgumentException`. The last page has no token.
- **A container that doesn't exist** lists as empty, without an exception; see
  [Reading from a container that doesn't exist](errors.md#reading-from-a-container-that-doesnt-exist).
- **Cost:** with SQL Server, each page is one query that starts where the last one stopped, so a page costs about the
  same wherever it is in the container.

## Finding resources by metadata

A listing can also find the resources whose current version has a metadata value, such as every invoice of one customer.
Name the keys you'll search by when you add Resourcefull, then search with `ResourceListOptions.MetadataMatch`:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseMetadataSearch("customer", "status"));

await foreach (ResourceInfo invoice in invoices.ListResourcesAsync(new ResourceListOptions
{
    MetadataMatch = new ResourceMetadataMatch("customer", "42"),
    Prefix = "2026/"   // optional, as in any listing
}))
{
    Console.WriteLine(invoice.Name);
}
```

The provider keeps an index of each named key's value for every current version, so a search reads only what matches
instead of every name in the container. Everything above about listings holds for a search too: it's in name order, by
page, with continuation tokens, which work only with the same prefix and the same key and value.

- **The match is exact:** the key and value are compared character by character, case-sensitively (ordinally), so a
  search for `customer` = `42` doesn't find the value `420` or the key `Customer`. Only the current version counts: a
  resource whose older version had the value isn't found.
- **Only named keys can be searched.** A search by another key throws `ArgumentException`, rather than reading the whole
  container. Indexing every key would make every write slower, and most applications search by only a few.
- **Named keys have limits** so the index can hold them: a key can be at most 128 characters and can't start or end
  with whitespace, and its value can be at most 256 characters. `UseMetadataSearch` throws `ArgumentException` for a
  key that breaks these rules. An upload, copy, restore, import or `SetResourceMetadataAsync` that gives a named key a
  longer value throws `ArgumentException` and stores nothing. A search for a longer value throws `ArgumentException`
  too. A longer value stored before you named its key stays as it is, but isn't indexed, so no search finds it. Other
  keys have no limit.
- **Adding a key later:** versions stored before you named it aren't in its index yet. Index them once for each
  container, such as from a maintenance job; writes from then on are indexed as they're stored:

  ```csharp
  await resources.GetContainerClient("invoices").IndexMetadataAsync();
  ```

- **What it costs:** with SQL Server, PostgreSQL and SQLite, once you name a key, every write updates the index in the
  same transaction, which takes a few more round trips, and a search page is one query. With the file system, S3 and
  Azure Blob, each named key's value is a small index entry of its own, written with the version, and a delete or
  metadata change makes one more read of the name first. A search reads each name it finds to make sure the value is
  still current, so a page of 100 matches makes about 100 reads. With the in-memory provider, or a [provider of your
  own](providers/writing-a-provider.md#metadata-search) that keeps no index, a search reads every name.
