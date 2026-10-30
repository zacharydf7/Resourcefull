# Going to production

Everything to set up before real users and real data arrive, in one place. Each item says why it matters in a sentence
and links to the page with the details. Work down the list once before your first release, and again whenever you add
a feature such as shared links or expiry.

## The database

- [ ] **A SQL Server you can run in production.** The free Express edition stops at 10 GB per database, which files
  reach quickly. See [Running SQL Server](providers/sql-server-operations.md#choosing-a-sql-server).
- [ ] **Provisioning as a deployment step,** not at startup of every instance: the first provisioning turns on a
  database setting that briefly disconnects every other session. See
  [Provisioning](providers/sql-server.md#provisioning).
- [ ] **A login with only the permissions the application needs** (`db_datareader` and `db_datawriter`), and a
  separate one for provisioning. See [Permissions](providers/sql-server.md#permissions).
- [ ] **The connection string kept out of source control,** in user secrets, environment variables or a secrets store.
- [ ] **Backups, and a restore you've tried.** Files are only as safe as your last backup. See
  [Backups and recovery](backups-and-recovery.md).
- [ ] **Room to grow:** the transaction log and `tempdb` sized for your largest uploads and deletes. See
  [Running SQL Server](providers/sql-server-operations.md#sizing).

## The application

- [ ] **Every file endpoint secured:** sign-in, each user kept to their own containers, names you choose, upload
  limits. See [Securing file endpoints](aspnet-core/securing-endpoints.md).
- [ ] **The exception handler registered,** so missing files answer 404 and broken rules 400, 409 or 413, rather than
  500. See [Turning exceptions into status codes](aspnet-core/status-codes.md).
- [ ] **Request size limits raised only for the endpoints that take large files,** and in any proxy in front of the
  application. See [Large uploads](aspnet-core/accepting-uploads.md#large-uploads).
- [ ] **Container limits** for anything users can fill: `MaxResourceSize`, `MaxContainerSize`, and the file types each
  container takes. See [Container policies](container-policies.md).
- [ ] **The same `UseVersioning` setting in every application and worker** that uses the database: one without it
  deletes the history of every name it uploads to. See [Versioning](versioning.md#changing-the-setting).
- [ ] **Shared links:** Data Protection keys stored where every instance can read them, so links keep working across
  servers and restarts. See [Shared links in production](sharing-links-in-production.md).
- [ ] **Expiry:** `UseExpiry` turned on in the applications that should delete expired files, and a cleanup handler if
  you need a record of what's deleted. See [Expiring resources](expiry.md).

## Watching it run

- [ ] **Health checks** for readiness, so traffic stops going to an instance that can't reach the database or runs
  ahead of its schema. See [Health checks](health-checks.md).
- [ ] **Telemetry** collected: Resourcefull's spans and metrics, under the name `Resourcefull`. See
  [Telemetry](telemetry.md).
- [ ] **Alerts** on what needs a person:
  - failed operations: `resourcefull.client.operation.duration` measurements with an `error.type` tag, especially
    `Microsoft.Data.SqlClient.SqlException` and EF Core's `RetryLimitExceededException`, which mean the database is in
    trouble;
  - errors logged under `Resourcefull.ExpiryService` ("Deleting expired versions failed"), and warnings under
    `Resourcefull.ResourceContainerClient` (a failed prune), which mean files aren't being deleted when they should be;
    the same show up as counters, with SQL Server's retries, in
    [Work that happens out of sight](telemetry.md#work-that-happens-out-of-sight);
  - the readiness health check reporting Unhealthy, or Degraded for long after a deployment;
  - the database's free space and log space, from SQL Server's own monitoring.
- [ ] **A runbook** for the incidents a file store has: a full transaction log, a full disk, running out of
  connections. See [Running SQL Server](providers/sql-server-operations.md#when-something-goes-wrong).

## Upgrading

- [ ] **Each release's notes read** before upgrading, in the [changelog](../CHANGELOG.md), and the database upgraded
  before the application. See [Upgrading the database](providers/sql-server-upgrades.md).
