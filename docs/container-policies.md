# Container policies

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Container policies limit what can be written to a container: how large a file can be, how much a container can hold,
how many files it can have, which types of file it accepts, and whether it can change at all. They also set how many
versions of each file it keeps. Set defaults for every
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
| `AllowedContentTypes` | upload, copy and move in, changing a content type <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | is refused before any content is read when its type isn't in the list, declares no type, or isn't a single valid type (such as `image/png, text/html`). `image/*` includes SVG images, which can run scripts when shown, so list exact types for files you display |
| `ReadOnly` | upload, restore, copy and move in or out, delete, deleting the container, changing an expiry, metadata <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> or content type <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, changing the container's metadata <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, bringing back or purging a version from the [bin](deleting-safely.md#the-bin) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | is refused before anything changes; and [expiry](expiry.md) leaves its expired versions, and its bin, until it's writable again |

A refused write throws `ResourcePolicyViolationException`, whose `Rule` says which rule it broke, and stores nothing.
A restore, copy or move is checked for every version it brings, by its stored length (measured from its content,
without reading it, for a version an early release stored without one), since its content doesn't pass through your
application; a rename within a container changes nothing it holds, so only `ReadOnly` applies to it. A length an
early release recorded wrongly isn't caught this way: the [hash backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions)
reports those.

- **Sizes count every version.** `MaxContainerSize` includes older versions until they're deleted, including a
  pruned version's five minutes with [expiry](expiry.md) on, so with [versioning](versioning.md) a container's history
  counts against it. Pruning is never refused. Replacing a resource needs room for the new content while the old
  version still counts, even without versioning.
- **Content types** are compared without their parameters and ignoring case, so `application/pdf` allows
  `Application/PDF; charset=binary`. End a type with `/*` to allow every subtype, such as `image/*`.
- **Lowering a limit** below what a container holds deletes nothing: it only refuses new writes.
- **`MaxVersions`** isn't a rule a write can break: it's how many versions of each name this container keeps, in
  place of the store-wide [`UseVersioning`](versioning.md) setting. Lowering it deletes each
  name's extra versions at the name's next upload, or at once with the container client's `PruneAsync`.
- **`RetainFor`** <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> isn't one either: it's how long each new version in the container can't be deleted, counted
  from when its write starts, unless the write gives its own `RetainUntil`. Versions already stored keep their dates.
  See [Deleting safely](deleting-safely.md).
- **`KeepDeletedFor`** <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> isn't one either: it's how long a deleted or pruned version stays in the container's
  [bin](deleting-safely.md#the-bin), where it can be brought back, before the expiry sweep purges it. It needs
  [`UseExpiry`](expiry.md). The bin doesn't count toward `MaxContainerSize` or `MaxResourceCount`, so deleting makes
  room, and bringing versions back is checked against both.
- **Writes at the same time can go slightly over.** The size and count limits read what the container holds once per
  write, so uploads running at the same moment can together pass `MaxContainerSize` or `MaxResourceCount` by up to
  one upload each. Set `MaxResourceSize` alongside `MaxContainerSize` to bound that: without it, each of those uploads
  may use all the room that's left. A move checks the versions it moves: if an upload to the source name makes a new
  version current while it moves, it checks the policy again with that version. Like an upload, it can still go over
  alongside other writes to the destination. For hard caps, such as billing, compare `GetUsageAsync` with the plan when
  it matters.

## Sizes

`ResourceSize` is how sizes are written. Its units are 1,024-based, as Windows shows file sizes:

```csharp
ResourceSize.Kilobytes(512)
ResourceSize.Megabytes(100)    // 104,857,600 bytes
ResourceSize.Gigabytes(5)
MaxResourceSize = 1_048_576    // a number of bytes works too
```

## Defaults from configuration

`UseConfiguration` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> reads the defaults, and how
long policies are cached, from a section of your configuration, with sizes written as text:

```json
{
  "Resourcefull": {
    "ContainerPolicies": {
      "Defaults": {
        "MaxResourceSize": "100 MB",
        "MaxContainerSize": "5 GB",
        "MaxResourceCount": 10000,
        "AllowedContentTypes": [ "image/*", "application/pdf" ]
      },
      "CacheFor": "00:00:05",
      "KeepLastKnownFor": "00:05:00"
    }
  }
}
```

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseConfiguration(builder.Configuration.GetSection("Resourcefull")));
```

The `ContainerPolicies` section turns policies on, and anything left out keeps its default. An empty
`AllowedContentTypes`, `[]` or `null`, allows every type, as leaving it out does; to refuse every write, set
`"ReadOnly": true`. A key that isn't one of these settings, such as a misspelled `MaxResourceSise`, stops the
application at startup with the key's name, rather than being ignored. The same section can hold the other settings;
see [Settings from configuration](getting-started.md#settings-from-configuration).

A size is a number followed by `B`, `KB`, `MB`, `GB` or `TB` (or `KiB` and so on, which mean the same), such as
`"1.5 GB"`; a number alone is bytes. `ResourceContainerPolicy` also binds on its own, with
`GetSection(...).Get<ResourceContainerPolicy>()`, for a policy you keep somewhere else in your configuration.

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
- **If it fails,** such as when its database is down, a container's policy as it last gave it is used instead, for up
  to 5 minutes after it gave it, so writes carry on. After that, or for a container it hasn't given a policy for yet,
  writes throw its exception. Pass `keepLastKnownFor` to `UseContainerPolicies` to change how long, or
  `TimeSpan.Zero` to fail at once; it helps only when longer than `cacheFor`. The cost: a change made just before the
  outage, such as making a container read-only, may not apply until the provider answers again.

## Usage and limits

`GetUsageAsync` reports what a container holds, and `GetPolicyAsync`
<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> the limits its writes are checked against, so
you can show users their storage against their plan:

```csharp
ResourceContainerUsage usage = await container.GetUsageAsync(cancellationToken);
// usage.StoredSize, such as "1536 MB", usage.ResourceCount and usage.VersionCount
ResourceContainerPolicy policy = await container.GetPolicyAsync(cancellationToken);
// policy.MaxContainerSize, such as "5 GB", or null for no limit

string shown = policy.MaxContainerSize is { } max ? $"{usage.StoredSize} of {max}" : $"{usage.StoredSize}";
```

`GetPolicyAsync` gives the policy as a write sees it: your defaults, with each limit the policy provider sets for
the container in place of the default. It's cached, and stands in for the provider while it fails, as for writes.
Its `MaxVersions` is always the version limit in force, from the policy, the defaults or `UseVersioning`, or 1 when
none of them sets one. Without container policies, that's the only limit it sets.

With SQL Server it's one query over the container's version rows, without reading content. The size and count
limits read it once per write they check, so for containers with very many versions, expect each limited upload to
cost that query too.

When a container nears its `MaxContainerSize`, `GetTopVersionsAsync`
<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> shows what's filling it: its largest versions,
current and older alike, since the stored size counts every version.

```csharp
IReadOnlyList<ResourceInfo> largest = await container.GetTopVersionsAsync(ResourceVersionOrder.LargestFirst, 10, cancellationToken);
// Each one's Name, VersionId, ContentLength, and IsLatest: false for an older version
```

It's meant for an administrator looking now and then, such as on the [dashboard](aspnet-core/dashboard.md), not for
every request: it reads all of the container's versions, in one query with SQL Server, PostgreSQL and SQLite.

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
policy provider records a `resourcefull get_policy` child span, only when the cache misses, and `GetUsageAsync` and
`GetPolicyAsync` record `get_usage` and `get_container_policy` operations. When the provider fails and the last known policy is used, its span records the failure and
`resourcefull.policy_last_known`, and `resourcefull.client.policy.last_known_uses` counts it.
See [Telemetry](telemetry.md).
