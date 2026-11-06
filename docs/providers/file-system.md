# File system

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

`Resourcefull.FileSystem` stores resources as files in a directory, on a local disk or a network share. No database or
storage service is needed: give it a directory, and every name keeps its versions, content hashes and metadata, as with
the other providers. It suits a single server, a small deployment, a development machine, or anywhere the files must
stay on your own disks. It's tested on Linux, Windows and macOS.

```shell
dotnet add package Resourcefull.FileSystem
```

```csharp
using Resourcefull;

builder.Services.AddResourcefull(options => options.UseFileSystem("/var/lib/my-app/files"));
```

The directory must exist; Resourcefull doesn't create it, so a share that isn't mounted, or a mistyped path, fails
loudly instead of filling up an empty directory somewhere else. A relative path is taken from the current directory
when the application starts.

## What it supports

Uploads, downloads (with ranges), listing, versions, create-only and conditional writes, conditional deletes, copies,
moves and renames, container and version metadata, [container policies](../container-policies.md),
[export and import](../backup-and-move.md), [expiry](../expiry.md) and [upload sessions](../uploading-in-parts.md). It
passes the same contract tests as the other providers, so switching to it, or from it, takes only the line that picks
the provider. [Provider features](features.md) compares the providers.

It works the way the [S3](s3.md) and [Azure Blob](azure-blob.md) providers do, and shares their code: where they keep an
object, it keeps a file.

## Choosing a directory

Give Resourcefull a directory of its own. Everything it writes goes under it. A file something else puts there may be
taken for one of Resourcefull's, and listed or deleted with them, unless its name is one Resourcefull never writes, such
as `desktop.ini` or `.DS_Store`: those are left alone and never listed.

The account the application runs as needs to read, write and delete files, and make and delete directories, under the
directory; nothing outside it.

**A local disk** is the simplest choice. Any filesystem works: NTFS, ext4, XFS, APFS and the rest. Resourcefull escapes
names so every filesystem keeps them exactly as given; see [Names](#names) below.

**A network share** lets several servers store in one place. Every server must reach the same files under the same
directory, and the share must honor the locks one machine takes on a file against the others, because that's how
Resourcefull keeps two writers from both changing a name at once. A Windows file share (SMB) reached from Windows does.
Other network filesystems, such as NFS, vary in whether a lock taken on one machine holds on the others; don't share one
across machines unless you've confirmed it does. On Linux and macOS, .NET takes those locks itself, so don't set
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING`, which turns them off.

**Several applications or instances** on one machine can share a directory. They take turns through the same locks.

## Backups

The directory is everything Resourcefull stores, so backing it up backs up the store. Copy it while nothing writes to
it, or take a snapshot of the volume, which captures every file at one instant, and restore it while the application is
stopped. A copy made file by file while writes go on can catch a name's list of versions and their content at different
moments, so the restored copy might list a version whose content it doesn't hold. [Export](../backup-and-move.md) reads
each version whole while the application runs, which suits moving or archiving a container; [Backups and
recovery](../backups-and-recovery.md) explains why it's no substitute for a backup.

## How it stores things

You don't need this to use the provider, but it explains its costs and what you'll see in the directory.

The layout is the same as the [S3 provider's](s3.md#how-it-stores-things), with a file for each object and a directory
for each `/` in a key:

- **Each container has a record**, `c/{name}`, holding a random ID and the container's metadata. Everything the
  container holds is kept under that ID, at `d/{id}/`.
- **Each name has a head**, a small JSON file listing its versions' attributes in the order they were stored; the last
  is current. Every change to a name (an upload, a delete or a metadata change) reads the head and writes it back only
  if nobody changed it meanwhile. When somebody did, it reads it again and retries.
- **Each version's content** is a file of its own, written before the head names it, and deleted after the head stops
  naming it.
- **Each version that expires has an entry** under `x/`, keyed by when it expires, so the [expiry](../expiry.md) sweep
  reads only what's due.
- **Each version in a [bin](../deleting-safely.md#the-bin)** <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> stays in its name's head, separately from the
  name's versions, and its content stays where it was. It has two entries, written just before the head write that puts it in
  the bin: one under `d/{id}/t/`, newest deletion first, whose key holds its content's length, for listings
  of the bin and the usage figures, and one under `t/`, keyed by when it's purged, for the sweep. Both are
  checked against the head when read, and one whose version is no longer in the bin is dropped once it's over an hour
  old, as expiry entries are; the daily scan for content no head names also drops stale bin entries that no listing reaches. A name left with only a
  bin has a mark under `d/{id}/e/`, so the usage figures can leave it out of the names, and the same scan fixes any mark a failed write left wrong.
- **Each [upload session](../uploading-in-parts.md)** has a record under `u/` and a file per segment beside it. Each
  append stores its content in segments of up to 64 MiB; completing copies them, in order, into the version's content
  file.
- **A move** writes an entry under `m/`, marks both names' heads, then commits the entry with one write, so a move's
  versions are never in both places or in neither.

Two directories are the provider's own. `.tmp` holds files being written: every write goes to a file there first,
flushed to disk, then moved into place in one step, so nobody ever reads a file half-written, even after a crash or a
power cut. A write that stops partway leaves its file in `.tmp`; writes look for such files at most once an hour, and
delete those over a day old. `.locks` holds 256 small lock files. A write that must not race another, such as a change
to a name's head, holds one of them while it compares and writes, so writers in every process sharing the directory take
turns. Lock files are never deleted, and stay empty.

Once a delete empties a directory, the directory is deleted too, and so on up to the directory you gave Resourcefull.

### Names

Each part of a key becomes a file name, escaped so it means the same on every filesystem. Lowercase ASCII letters,
digits and most punctuation stay as they are. Anything else is escaped:

- An uppercase letter becomes `^` and the letter in lowercase, so `Report.pdf` and `report.pdf` stay two names even on
  Windows and macOS, whose filesystems ignore case by default.
- A character beyond ASCII becomes `~u` and four hex digits (`~v` and six beyond U+FFFF), so `é` written as one
  character and as `e` with an accent mark stay two names even on macOS, which may treat them as one.
- Any other character a filesystem might refuse or change, such as `:`, `?` or a space, becomes `~` and two hex digits.

A long name is split across nested directories, so no file name is longer than the 255 characters filesystems allow. A
whole path has a limit too: macOS takes at most 1,023 bytes, and each character beyond ASCII takes six in a file name,
so on macOS a name of more than about 150 such characters doesn't fit, and fewer when the path to the directory is long.
Storing one throws `ResourceNameException`, which names the limit, and looking one up finds nothing. Linux and Windows
take paths several times longer, enough for any name.

Names are listed in Unicode code point order, as with PostgreSQL and S3. That differs from .NET's ordinal order only
between characters beyond U+FFFF, such as emoji, and those from U+E000 to U+FFFF.

### What stays behind

- A deleted container keeps its record, marked deleted, and a name whose versions are all deleted keeps an empty head.
  Both are a few bytes, and are reused if the name is created again.
- A write interrupted after its content was written, such as by a crash, leaves that content named by no head. The
  [expiry](../expiry.md) sweep deletes it once a day, as with S3, and also finishes purging a container whose delete
  stopped partway, and finishes or undoes a move whose mover stopped.
- A deleted container's upload sessions stay under `u/` until the next expiry sweep deletes them.
- On Windows, a directory can be left empty when its last file was being read as it was deleted. It's harmless, and goes
  when a later delete in it finds it empty.

## Costs

The provider does all its work through the filesystem, so its costs are file reads and writes, and the memory a
listing takes.

- **An upload** reads the container's record, writes the content to a file, flushes it to disk and moves it into place,
  then reads and writes the head under its lock. A create-only or conditional upload, which includes the default mode,
  also reads the head before writing the content. Content streams to disk 80 KiB at a time, so memory stays flat
  whatever the size. There's no limit on a file's size beyond the disk's.
- **A download** reads the container's record and the head, then streams the file; a range reads only that part.
- **A listing page** reads the directory of heads, sorts it, then reads the heads on the page. A directory is read
  whole for each page, since filesystems list in no particular order, so a container's listing slows as it grows. It's
  quick for tens of thousands of names; with millions in one container, each page takes a while, and holds the names of
  the directory in memory as it sorts them.
- **Usage**, which [container policies](../container-policies.md) check before each write they limit, comes from
  reading the container's directories, without opening the files.
- **An expiry sweep** with nothing due reads three directories (of expiry entries, upload sessions and moves) and one
  file, the record of the last scan for unnamed content. Once a day, it reads every container's record, head and
  content file's size.
- **A rename** writes and moves a few small files and copies no content. **A move to another container** copies each
  version's content into a new file, through your application.
- **An upload session** writes each segment as a file, and completing copies the segments into the version's content
  file through your application, so the session's content is written twice. A session holds as much as the disk has room
  for.
- **Writes that share a lock** take turns: two different names can land on the same one of the 256 locks, and then wait
  for each other while each writes its head, which takes a few milliseconds.

## Health checks

The [generic health check](../health-checks.md), `AddResourcefullCheck()`, makes one read through the provider, so it
reports whether the directory exists and can be read. A share that isn't mounted, or a directory that was deleted,
fails it.

## Telemetry

Besides the operations every provider records (see [Telemetry](../telemetry.md)), the provider counts
`resourcefull.filesystem.retries`: writes to a name's head, or to an upload session's record, retried because another
writer changed it first. A few are normal when writers share names; a steady rise means many writers are contending
for the same names. It counts `resourcefull.filesystem.moves_recovered`: moves the expiry sweep finished or undid
because their mover stopped first, such as after a crash. It also counts `resourcefull.filesystem.stale_expiries`:
expiry entries a sweep dropped because their version no longer existed or had another expiry;
`resourcefull.filesystem.stale_bin_entries`: bin and purge entries dropped because their version was no longer in its
bin; and `resourcefull.filesystem.abandoned_uploads`: upload sessions nothing was appended to for a day, or whose container was
deleted, and content no head names, which the expiry sweep deleted. Upload sessions never need joining in the store, so
`resourcefull.filesystem.session_joins` stays at zero.
