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
<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> In 3.1, `GetResourcesAsync` does that; 4.0 removes it, because it reads the whole container into memory
at once.

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
  as an emoji, after those from U+E000 to U+FFFF rather than before; see [PostgreSQL](providers/postgresql.md). S3 lists
  in code point order too, and Azure Blob in whatever order Azure returns; see
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
