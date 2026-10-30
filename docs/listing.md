# Listing resources

`GetResourcesAsync` returns every current resource in a container in one list, which suits small containers. For
large ones, or to list only the names under a prefix, enumerate `ListResourcesAsync`, which fetches a page at a time
as you go:

```csharp
await foreach (ResourceInfo invoice in invoices.ListResourcesAsync(new ResourceListOptions { Prefix = "2026/10/" }))
{
    Console.WriteLine(invoice.Name);
}
```

An API that pages for its own callers fetches one page per request and hands back the continuation token:

```csharp
ResourcePage page = await invoices.GetResourcePageAsync(
    new ResourceListOptions { Prefix = prefix, PageSize = 100 }, continuationToken, cancellationToken);
return Results.Ok(new { page.Resources, page.ContinuationToken });
```

- **Order:** by name, in ordinal order (`StringComparer.Ordinal`). Uppercase letters come before lowercase ones, and
  `2026/10` before `2026/10/a`. One exception: SQL Server compares names as if the shorter were padded with spaces,
  so a name followed by a character below U+0020 sorts before the name itself: `a` followed by a tab and `b` comes
  before `a`. A listing still returns every name exactly once; only the order of such names differs from the
  in-memory provider.
- **The prefix** is matched ordinally and case-sensitively, with every character taken literally, including `%`, `_`
  and `[`. Containers don't nest, so `2026/10/` is just the start of a name.
- **Page size:** from 1 to 5,000; 1,000 by default.
- **No snapshot.** Each page is a query of its own. A name that exists for the whole listing appears exactly once,
  and one created or deleted during it may or may not, so a listing never holds up uploads.
- **Continuation tokens** are opaque, URL-safe strings of at most about 1 KB, and they don't expire. Continue with the
  same prefix; the page size can change from page to page. A token from a listing of another prefix throws
  `ArgumentException`. The last page has no token.
- **Cost:** with SQL Server, each page is one query that starts where the last one stopped, so a page costs about the
  same wherever it is in the container.
