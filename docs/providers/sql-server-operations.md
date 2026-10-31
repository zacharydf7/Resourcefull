# Running SQL Server

What to know once the SQL Server provider is in production: which SQL Server to run, how big the database gets, how
uploads use connections, and what to do when something goes wrong. [SQL Server](sql-server.md) covers setting it up,
and [How SQL Server stores files](sql-server-storage.md) the details of storage.

## Choosing a SQL Server

- **Versions.** Resourcefull is tested with SQL Server 2019 and 2022 on every change, and with LocalDB in development.
  It uses only standard features (row versioning, application locks, filtered indexes), so SQL Server 2016 and later,
  Azure SQL Database and Azure SQL Managed Instance are expected to work, but aren't tested yet; reports either way
  are welcome.
- **Editions.** Express is free but stops each database at **10 GB** of data, which stored files reach quickly, and
  uses at most about 1.4 GB of memory. Use it for development and small installations; production usually needs
  Standard or above, or a hosted database.
- **LocalDB** is for development on Windows, not for servers.

## Sizing

Files take a little more room in the database than on disk, and moving them in and out uses the transaction log and
`tempdb`. These figures were measured on SQL Server; use them as a starting point and watch your own.

| What | Room it takes |
|---|---|
| Stored content | about 1.15 times the files' size, times the versions you keep |
| Transaction log, while uploading | about 1.13 times each upload, until the log is backed up or truncated |
| Transaction log, while deleting | about 1.07 times what's deleted |
| `tempdb`, while deleting | about 2 times what's deleted, briefly, for the row versions readers might still need |

A worked example: 50,000 files averaging 2 MB, keeping up to 5 versions, is at most 50,000 × 2 MB × 5 × 1.15, about
575 GB of data. Size the log for your busiest hour of uploads between log backups, and `tempdb` for a few of your
largest deletes at once.

`tempdb` grows because the database runs with `READ_COMMITTED_SNAPSHOT`, which keeps the old version of each row a
delete removes until no reader needs it. With [Accelerated Database Recovery](https://learn.microsoft.com/sql/relational-databases/accelerated-database-recovery-concepts)
turned on, those versions are kept in the database itself instead.

## Uploads, connections and the log

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

An upload holds no transaction, lock or connection while its content arrives, however slowly the client sends it:

1. **It starts** by storing the new version, marked as staged, which hides it from every read, listing and usage
   total.
2. **The content goes in a batch at a time,** about a MiB each, on a connection taken from the pool for that batch
   and given back. Each batch commits as it's stored, so the log can truncate past it.
3. **It finishes** with a short transaction that takes the name's lock, checks the upload's conditions and limits, and
   makes the version current.

Downloads work the same way: a connection for each page of content, given back after it.

What that means for slow clients:

- **Connections don't run out.** A thousand slow uploads hold no more connections between batches than they're
  storing at that moment. Keep Kestrel's minimum upload rate on (`MinRequestBodyDataRate`, 240 bytes a second by
  default) anyway, so a stalled client is disconnected rather than kept waiting.
- **The log isn't held back.** Each batch's log can be truncated, or backed up under the full recovery model, while the
  rest of the upload is still arriving. The upload writes as much to the log in all as it did before.
- **A stopped upload leaves its batches behind** when its process dies before it can delete them. The
  [expiry](../expiry.md) sweep deletes a staged version an hour after its upload last stored anything. An upload
  marks itself running every 10 minutes as its content arrives, so it's safe as long as a chunk, about 40 KB, arrives
  every 50 minutes: far slower than Kestrel's minimum rate lets a request body go. Without `UseExpiry`, call
  `DeleteExpiredAsync` on a schedule, or such batches stay until you do. `resourcefull.sqlserver.abandoned_uploads`
  counts what the sweep deletes.

Before 3.2, an upload was one transaction, holding a connection and the log for as long as the client took.

## Timeouts

Each statement has 30 seconds by default (`Command Timeout` in the connection string, or the `Connect Timeout` for
opening a connection). Copying a large file is one statement that copies all of its content inside the server, which
for files of several GB can take longer. If you copy or move large files, raise `Command Timeout`, for example to
`Command Timeout=300`.

Transient failures, such as a dropped connection, a failover or a deadlock, are retried automatically, up to 5 times;
see [Retries](sql-server.md#retries).

## When something goes wrong

Symptom, likely cause, and what to do.

**Uploads and deletes fail with SQL error 9002, "The transaction log for database … is full".** The log can't grow:
it's at its maximum size, or the disk is full. Back up the log (in the full recovery model, only a log backup lets it
truncate), add space, or raise its maximum size. Then check for a long-running transaction holding it, with
`SELECT log_reuse_wait_desc FROM sys.databases WHERE name = '<database>'`: `ACTIVE_TRANSACTION` points at a slow upload.

**Uploads fail with error 1105, "Could not allocate space for object …".** The data file or its disk is full. Add
space or raise the file's maximum size. On Express, this is the 10 GB limit.

**Everything times out with "Timeout expired. The timeout period elapsed prior to obtaining a connection from the
pool".** Every pooled connection is busy, usually with slow uploads. See
[Uploads, connections and the log](#uploads-connections-and-the-log).

**Large copies or deletes fail with "Execution Timeout Expired".** The statement outran `Command Timeout`. Raise it;
see [Timeouts](#timeouts).

**After a deployment, requests fail with error 207, "Invalid column name …", and the health check is Unhealthy.** The
application was deployed before the database was upgraded. Run the upgrade
([Upgrading the database](sql-server-upgrades.md)); the readiness check keeps traffic away until then.

**The health check reports Degraded after a deployment.** The database was upgraded by a newer release than this
instance; that's expected while a rolling deployment finishes. If it lasts, an old instance is still running.

**Operations fail with EF Core's `RetryLimitExceededException`.** A transient failure kept happening through all 5
retries: the database is down, failing over, or unreachable. Its inner exception says which.

**Everything fails with error 3906, "Failed to update database … because the database is read-only".** The application
is connected to a read-only replica or a database set read-only. Point it at the primary.

**Expired files aren't being deleted.** Check the logs for `Resourcefull.ExpiryService`: an error repeats each interval
while sweeps fail, and a warning at startup means the provider doesn't support expiry. See
[Expiring resources](../expiry.md).
