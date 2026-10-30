# Contributing

Thanks for helping with Resourcefull. Bug reports, questions and ideas are welcome as
[issues](https://github.com/zacharydf7/Resourcefull/issues); for a larger change, open an issue to discuss it before
you start. Report a security vulnerability privately instead, as
[SECURITY.md](https://github.com/zacharydf7/Resourcefull/blob/main/SECURITY.md) explains.

Everyone taking part is expected to follow the
[code of conduct](https://github.com/zacharydf7/Resourcefull/blob/main/CODE_OF_CONDUCT.md).

## Building and testing

You need the .NET SDK that `global.json` names, or a later patch of the same feature band.

```shell
dotnet build
dotnet test tests/Resourcefull.Tests.Unit
dotnet test tests/Resourcefull.Tests.Integration
```

The unit tests need nothing else. The integration tests need SQL Server and create a throwaway database per run:

- **On Windows,** they use LocalDB (`(localdb)\MSSQLLocalDB`), which comes with Visual Studio.
- **Anywhere,** point them at a server with the `RESOURCEFULL_TEST_SQLSERVER` environment variable, for example a
  Docker container:

  ```shell
  docker run -d -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<a strong password>' mcr.microsoft.com/mssql/server:2022-latest
  export RESOURCEFULL_TEST_SQLSERVER="Server=127.0.0.1,1433;User Id=sa;Password=<a strong password>;TrustServerCertificate=True;Encrypt=False"
  dotnet test tests/Resourcefull.Tests.Integration
  ```

  `RESOURCEFULL_TEST_SQL_READY_TIMEOUT_SECONDS` (default 90) sets how long they wait for a container to start.

Without either, the integration tests are skipped, not failed. CI runs both suites, against SQL Server in Docker, on
every push and pull request.

### Performance tests

`tests/Resourcefull.Tests.Performance` measures uploads, downloads, listings and deletes through the public clients
against SQL Server: the round trips each makes (commands, savepoints, and beginning and committing transactions), the
logical reads they cost, the bytes it writes to the transaction log, the bytes it allocates, the memory it still holds
once it's done (after collecting the heap, so garbage doesn't count), and how long it takes. It needs SQL Server like
the integration tests:

```shell
dotnet test tests/Resourcefull.Tests.Performance -c Release --logger "console;verbosity=detailed"
```

CI runs it on every push and pull request against `baseline.json` in that project, on a pinned SQL Server image, and
fails when a scenario gets worse than its baseline allows:

| Measurement | Fails when |
|---|---|
| Round trips | any more than the baseline |
| Logical reads | more than 10% (plus 2 pages) over the baseline |
| Transaction log bytes | more than 10% (plus 64 KiB) over the baseline |
| Allocated bytes | more than 10% over the baseline |
| Retained memory | more than 50% (plus 256 KiB) over the baseline |

Time is reported but never fails the build: CI's shared runners vary too much from run to run for it to. Moving the
performance job to a newer SQL Server image is a deliberate change that comes with a new baseline.

Locally the comparison is only reported, since your machine and SQL Server differ from CI's; set
`RESOURCEFULL_PERF_ENFORCE=true` to fail as CI does. Every CI run uploads its measurements as the
`performance-results` artifact. When a change makes a scenario cheaper, or costs more on purpose, copy that artifact's
`performance-results.json` over `baseline.json` in the same pull request and say why in the commit, so the new numbers
are what later changes are held to. Once `baseline.json` has entries, a new scenario fails its first CI run for lack
of one; copy its entry from that run's artifact into `baseline.json`.

### How the tests are organized

- `tests/Resourcefull.Tests.Contract` is one suite of behavior every provider must share. It runs against the
  in-memory provider in the unit tests and against SQL Server in the integration tests, so a behavior added to one
  provider should get a contract test.
- `tests/Resourcefull.Tests.Unit` covers the clients and each provider's internals.
- `tests/Resourcefull.Tests.Integration` covers what needs a real SQL Server: concurrency, retries, cancellation,
  provisioning scripts and migrations.
- `tests/Resourcefull.Tests.Performance` holds the cost of uploads, downloads, listings and deletes to a committed
  baseline (above).

## Changing the database schema

The schema comes from EF Core migrations in `src/Resourcefull.SqlServer/Migrations`. The SQL scripts under
`src/Resourcefull.SqlServer/Scripts/` are generated from them, and the integration tests fail if a script doesn't
match.

1. Change the model, then add a migration:

   ```shell
   dotnet tool install --global dotnet-ef
   dotnet ef migrations add <Name> --project src/Resourcefull.SqlServer
   ```

   EF Core generates the migration as a `public` class; make it `internal`, like the others, so it doesn't become
   public API. A unit test fails otherwise.

2. Regenerate the scripts. Each one is the output of `dotnet ef migrations script --idempotent`, after a short preamble
   that's the same in every file:

   ```sql
   SET XACT_ABORT ON;
   GO

   ```

   `Resourcefull.sql` covers every migration, and each script under `Migrations/` covers one migration, from the one
   before it. When a script is out of date, the failing test prints the exact command that regenerates it.

3. Explain anything the migration needs from users in [docs/upgrading.md](docs/upgrading.md).

Name every new migration with `Resourcefull_` first (`dotnet ef migrations add Resourcefull_AddThing`): the history
table can be shared with an application's own migrations, and the health check counts only migrations whose name
starts this way as coming from a newer release. A test fails for a migration named otherwise.

Three rules keep the scripts generated and safe to run again:

- **Data fixes go in the migration**, as `migrationBuilder.Sql(...)`, never typed into a script.
- **Wrap raw SQL that reads a column added in the same migration in `EXEC(N'...')`.** A generated script runs each
  migration as one batch, and SQL Server binds column names when it compiles the batch, before the `ALTER TABLE` in
  the same batch has run.
- **Give a step that can't run in a transaction a migration of its own**, and make it check whether its work is
  already done. A `suppressTransaction: true` step, such as `ALTER DATABASE`, also moves the migration's history row
  out of its transaction, so a failure between the two leaves the change in place without a record of it. The
  integration tests stop each script after every batch and run it again, and fail if any of those runs can't finish.

## Conventions

- **American English** in code, comments, tests and docs.
- **Test names** follow `Method_ShouldExpectation_WhenCondition`, such as
  `UploadAsync_ShouldThrowConflict_WhenNameExistsAndOverwriteIsOff`.
- **Lines** wrap at about 120 characters; fill a line before wrapping parameters rather than putting one per line.
- **Public members** need XML documentation; the build fails without it.
- **Commit messages** are one short line in the imperative: "Add a content hash to every version".
- **Telemetry is part of done.** A change that adds or changes an operation records its span and metrics, adds
  the operation to [Telemetry](docs/telemetry.md), and has a test that observes them; the test that calls every
  public client method fails for a method that records no span.
- **Pull requests** keep to one change, with tests, and update the docs when behavior changes.
- **Unreleased features are labeled.** The docs site is published from `main`, so a page or section about something
  not yet on nuget.org says so, under its heading (not in it, so its link doesn't change):
  `<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>`. In tables and sentences,
  use `<span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>`. A page that's entirely new also gets a
  sidebar marker in `template/public/main.css`, and new public types and members get `<para>Coming in 3.2.</para>`
  in their XML docs, on a line of its own: on the same line as other text, the API reference shows it as code.

## Releasing

For maintainers:

1. **Before tagging,** check `<VersionPrefix>` in `src/Directory.Build.props` is the release's version (every build
   but the release tag's adds `-ci.<run>` or `-dev` to it). Turn the release's labels from upcoming to added: in
   `docs/`, `index.md` and `src/`, replace `Coming in 3.2` with `Added in 3.2` and `rf-upcoming` with `rf-added`,
   remove the sidebar markers from `template/public/main.css`, and move the README's "Coming in" list into its
   features. In `CHANGELOG.md`, turn `[Unreleased]` into `[3.2.0] - <date>`, start a new `[Unreleased]` section, and
   update the compare links at the bottom. In `docs/releases.md`, turn the "Coming in" section into the release's
   own, and update "What's next".
2. **Merge to `main`, then push the tag** (`v3.2.0`). The publish workflow runs every test, checks that the tag
   matches the version, publishes the packages it tested to nuget.org, and creates a GitHub Release from the
   version's changelog section.
3. **After it's published,** set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to the version just
   released, so the next release is checked against it, and `<VersionPrefix>` to the next version.

A release that adds a package ID needs it added to nuget.org's Trusted Publishing policy for this repository first, or
pushed once by hand: until then nuget.org refuses the workflow's push of the new ID with 403 Forbidden.
