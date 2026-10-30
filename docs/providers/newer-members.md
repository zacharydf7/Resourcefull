# Members added since 3.0

`IResourceRepository` gains members in minor releases, each with a default implementation, so a provider
written for an earlier release keeps compiling. Some defaults work, just more slowly than a provider could; others throw
`NotSupportedException`, so the feature that needs them reports that it isn't supported. Each section below says
which, and what to do to support the member directly. The core contract is in [Writing a provider](writing-a-provider.md).

## Listing by page

`GetResourcePageAsync` was added in 3.1. Its default implementation reads the whole container through
`GetAllResourceInfoAsync` and sorts and filters it in memory, so a provider written for 3.0 lists correctly, just
without the savings. To answer it directly, return at most `pageSize` current versions whose names start with
`prefix`, ordinally, and come after `afterName`, ordered by name. Use ordinal order, or your storage's binary order
if its ordering and its "after" comparison agree, so no name is skipped or repeated between pages.

## Reading ranges

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

The overload of `GetResourceStreamAsync` that takes an `offset` and a `length` was added in 3.2. Its default
implementation opens the whole content and reads past the bytes before `offset`, so a provider written for 3.1
returns correct ranges, just without the savings. To answer it directly, return a stream that starts at `offset`:

- **`Length` is still the content's full length,** so the client can report it, and `Position` starts at `offset`, or
  at the content's length when `offset` is past it.
- **Reading may go on past the range.** The client reads only `length` bytes, so a provider can stop reading storage
  there or carry on to the end, whichever is simpler.

## Copying and moving

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

`CopyResourceAsync` and `MoveResourceAsync` were added in 3.2, and `AddResourceAsync` should store
`ResourceInfo.CopiedFrom` with the version and return it on every read, as it does the content hash.

- **Copy's default implementation** reads the source and stores it through `AddResourceAsync`, so a provider written
  for 3.1 copies correctly, just through the application, and without `CopiedFrom` unless it stores it. Override it
  to copy within your store, deciding create-only writes and conditions at the destination as an upload does.
- **Move's default throws `NotSupportedException`.** A move must change every version's container and name at once,
  never leaving the resource in both places or half in each, so made of copies and deletes it could stop halfway.
  Implement it to keep each version's ID and attributes, refuse a destination name that has versions
  (`ResourceConflictException`) or whose container is missing (`ResourceContainerNotFoundException`), and decide the
  source conditions in the move itself.

## Importing versions

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

`ImportVersionAsync` was added in 3.2, for [importing a container](../backup-and-move.md). It stores a version as it was
in another store: with the `VersionId` and `LastModified` it's given, rather than new ones, and with its type, metadata,
`CopiedFrom` and `ExpiresAt`. Otherwise it's an upload: measure and hash the content as `AddResourceAsync` does, refuse
a wrong `ContentLength`, and make the version the name's current one. Its default throws `NotSupportedException`, so
imports fail with a provider written for 3.1; exports work with any provider, because they only read.

## Expiry

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

`GetExpiredAsync`, `DeleteExpiredVersionAsync` and `SetExpiryAsync` were added in 3.2, and `AddResourceAsync` and
`CopyResourceAsync` should store `ResourceInfo.ExpiresAt` with the version and return it on every read. Their default
implementations throw `NotSupportedException`, so [expiry](../expiry.md) logs a warning and stops with a provider
written for 3.1.

A provider written for 3.1 doesn't refuse an expiry, though: `AddResourceAsync` stores the upload and drops
`ExpiresAt`, and the clients can't tell, so the upload's result reports an expiry that will never happen. The same
goes for `CopiedFrom` on copies and restores, which is lost. Store both before anyone uses expiry with your provider.

To support it:

- **List expired versions from an index, a batch at a time.** Expiry calls `GetExpiredAsync` every interval on every
  instance, so it should cost little when nothing has expired. Order them by `ExpiresAt`, then ordinally by version
  ID, and start after the `after` version you're given: a sweep pages through every expired version this way, and
  fails if a provider lists the same batch twice.
- **Decide expiry in the delete.** `DeleteExpiredVersionAsync` deletes a version only if it's still expired when the
  delete runs, under whatever serializes writes to the name: `SetExpiryAsync` may have extended it since it was listed.
- **Change nothing but the expiry** in `SetExpiryAsync`.

## Container usage

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

`GetContainerUsageAsync` was added in 3.2. Its default implementation lists every resource's versions, one name
at a time, and adds up their `ContentLength`, so a provider written for 3.1 reports usage correctly, just slowly for
large containers. [Container policies](../container-policies.md) call it before each upload they limit by size or
count, so if your store can add up a container's versions in one query, override it. Count every version in
`StoredSize` and `VersionCount`, and each name once in `ResourceCount`.

## Conditional uploads and deletes

The overloads of `AddResourceAsync` and `DeleteResourceAsync` that take conditions were added in 3.1, with default
implementations, so a provider written for 3.0 still compiles. Those defaults throw `NotSupportedException` for an
upload or delete with `ResourceConditions`, so an application using them gets a clear error instead of an unchecked
write. The clients call these overloads only when a caller sets conditions. To support them, implement both overloads
and:

- **Decide the conditions in the write itself,** at the moment the version would become current, or under whatever
  serializes writes to the name, so no other write can come between the check and the change.
- **Treat a name with no current version as meeting no condition,** and empty conditions as none.
- **Throw `ResourcePreconditionFailedException`** with the current version's ID and content hash, and change nothing.
