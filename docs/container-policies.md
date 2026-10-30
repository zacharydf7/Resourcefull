# Container policies

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Container policies limit what can be written to a container: how large a file can be, how much a container can hold,
how many files it can have, which types of file it accepts, and whether it can change at all. Set defaults for every
container, and give any container its own policy, such as each user's storage plan, which can change while the
application runs.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseContainerPolicies(new ResourceContainerPolicy
    {
        MaxResourceSize = ResourceSize.Megabytes(100),
        AllowedContentTypes = ["image/*", "application/pdf"]
    })
    .UsePolicyProvider<PlanPolicyProvider>());   // optional
```

Without `UseContainerPolicies` or `UsePolicyProvider`, nothing is checked and nothing changes.

## The rules

| Rule | Checked on | A write that breaks it |
|---|---|---|
| `MaxResourceSize` | upload, restore, copy and move in | is refused before any content is read, if its declared length is over the limit; otherwise it stops as soon as its content passes the limit |
| `MaxContainerSize` | upload, restore, copy and move in | is refused before any content is read, if its declared length won't fit in what's left; otherwise it stops as soon as its content passes what's left |
| `MaxResourceCount` | upload, copy or move in of a new name | is refused when the container already holds that many resources; a new version of an existing name never is |
| `AllowedContentTypes` | upload, copy and move in | is refused before any content is read when its type isn't in the list, or when it declares no type |
| `ReadOnly` | upload, restore, copy and move in or out, delete, deleting the container, changing an expiry | is refused before anything changes; and [expiry](expiry.md) leaves its expired versions until it's writable again |

A refused write throws `ResourcePolicyViolationException`, whose `Rule` says which rule it broke, and stores nothing.
A copy or move is checked for every version it brings, by its stored length (measured from its content, without
reading it, for a version an early release stored without one); a rename within a container changes nothing it holds,
so only `ReadOnly` applies to it.

- **Sizes count every version.** `MaxContainerSize` includes older versions until they're pruned, so with
  [versioning](versioning.md) a container's history counts against it. Pruning is never refused. Overwriting a
  resource needs room for the new content while the old version still counts, even without versioning.
- **Content types** are compared without their parameters and ignoring case, so `application/pdf` allows
  `Application/PDF; charset=binary`. End a type with `/*` to allow every subtype, such as `image/*`.
- **Lowering a limit** below what a container holds deletes nothing: it only refuses new writes.
- **Writes at the same time can go slightly over.** The size and count limits read what the container holds once per
  write, so uploads running at the same moment can together pass `MaxContainerSize` or `MaxResourceCount` by up to
  one upload each. For hard caps, such as billing, compare `GetUsageAsync` with the plan when it matters.

## Sizes

`ResourceSize` is how sizes are written. Its units are 1,024-based, as Windows shows file sizes:

```csharp
ResourceSize.Kilobytes(512)
ResourceSize.Megabytes(100)    // 104,857,600 bytes
ResourceSize.Gigabytes(5)
MaxResourceSize = 1_048_576    // a number of bytes works too
```

## Defaults from configuration

`ResourceContainerPolicy` binds from configuration, with sizes written as text:

```json
{
  "Storage": {
    "DefaultPolicy": {
      "MaxResourceSize": "100 MB",
      "MaxContainerSize": "5 GB",
      "MaxResourceCount": 10000,
      "AllowedContentTypes": [ "image/*", "application/pdf" ]
    }
  }
}
```

```csharp
var defaults = builder.Configuration.GetSection("Storage:DefaultPolicy").Get<ResourceContainerPolicy>();
builder.Services.AddResourcefull(options => options.UseSqlServer(connectionString).UseContainerPolicies(defaults));
```

A size is a number followed by `B`, `KB`, `MB`, `GB` or `TB` (or `KiB` and so on, which mean the same), such as
`"1.5 GB"`; a number alone is bytes.

## A policy for each container

Implement `IResourcePolicyProvider` to give containers their own policies, such as a quota for each user's container
from their plan:

```csharp
public sealed class PlanPolicyProvider(AppDbContext db) : IResourcePolicyProvider
{
    public async ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken)
    {
        Plan? plan = await db.PlanForContainerAsync(containerName, cancellationToken);
        return plan is null ? null : new ResourceContainerPolicy { MaxContainerSize = plan.Storage };
    }
}
```

- **Return `null`** for the defaults alone. A property left `null` in the policy you return falls back to its
  default, so this provider sets each user's storage and keeps the default file types and file size.
- **It's a scoped service,** so it can use your `DbContext` and other services.
- **Its answers are cached** for 5 seconds per container, so it isn't called on every upload, and a change an admin
  makes applies within 5 seconds on every instance. Pass `cacheFor` to `UseContainerPolicies` to change that, or
  `TimeSpan.Zero` to ask on every write.

## Usage

`GetUsageAsync` reports what a container holds, to show users their storage or to check it against their plan:

```csharp
ResourceContainerUsage usage = await container.GetUsageAsync(cancellationToken);
// usage.StoredSize, such as "1536 MB", usage.ResourceCount and usage.VersionCount
```

With SQL Server it's one query over the container's version rows, without reading content. The size and count
limits read it once per write they check, so for containers with very many versions, expect each limited upload to
cost that query too.

## Over HTTP

With [`AddResourcefullExceptionHandler`](aspnet-core/status-codes.md), a refused write
becomes:

| Rule | Status |
|---|---|
| `MaxResourceSize`, `MaxContainerSize` | 413 Content Too Large |
| `MaxResourceCount` | 409 Conflict: the container is full until something is deleted |
| `ReadOnly` | 403 Forbidden |
| `AllowedContentTypes` | 415 Unsupported Media Type |

## Telemetry

A refused write's span has `error.type` and `resourcefull.policy_rule`, such as `max_container_size`. A call to your
policy provider records a `resourcefull get_policy` child span, only when the cache misses, and `GetUsageAsync` records
a `get_usage` operation. See [Telemetry](telemetry.md).
