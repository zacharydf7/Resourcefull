# Backups and recovery

Files are only as safe as your last backup, and a backup is only as good as the last time you restored one. This page
explains how to back up the files Resourcefull stores, how to get them back, and how that fits with the rest of your
application's data. It assumes the SQL Server provider.

## Back up the database

Resourcefull keeps everything in its SQL Server database, so **the database backup is the backup**. Use SQL Server's
own backups, or your hosting's: Azure SQL Database and most managed SQL Servers back up automatically and let you
restore to any moment in the last days or weeks.

If you run SQL Server yourself, the database's **recovery model** decides what you can restore to:

- **Simple:** you can restore to the moment of a full (or differential) backup, and lose what changed after it. The
  log looks after itself.
- **Full:** with regular log backups as well, you can restore to any moment, such as just before someone deleted the
  wrong container. Log backups are also what lets the log reuse its space, so without them it grows until the disk is
  full.

For files people would miss, use the full model with log backups every few minutes to an hour, plus a full backup
daily or weekly. Microsoft's [backup overview](https://learn.microsoft.com/sql/relational-databases/backup-restore/back-up-and-restore-of-sql-server-databases)
covers the commands.

## Keep it consistent with your own data

Most applications keep their own records of the files: an invoice row with the name of its PDF, or a version ID a
document points at. Restored separately, the two can disagree: a row pointing at a file that isn't in the restored
backup, or a file no row mentions.

- **Simplest:** keep Resourcefull's tables in your application's own database (see
  [Choosing the database](providers/sql-server.md#choosing-the-database)), so one backup holds both.
- **With two databases:** restore both to the same moment, which the full recovery model allows, and expect to tidy
  up a few files or rows from the minutes around it.

## Try a restore

Restore a backup to a spare server every so often, point a copy of the application at it, and open a few files. A
restore you've never tried is a guess: it's how you find out that backups were failing, or that one takes longer than
you can afford.

## Get back one container

A backup restores the whole database, but sometimes one customer's container is all that went wrong. To bring back
just that container:

1. Restore the backup alongside the live database, under another name.
2. Point a copy of your application, or a small console app, at the restored database, and
   [export](backup-and-move.md#exporting) the container to a file.
3. [Import](backup-and-move.md#importing) the file into the live store, under a new name if the container still
   exists there. An import keeps each version's ID and time, so anything that recorded them still finds them.
4. Point your application at the imported container, or delete the damaged one and import again under its name.
5. Drop the restored database.

## Where container exports fit

[Exporting a container](backup-and-move.md) writes it to a zip file: every version of every file, with their IDs. That
is useful for moving a container to another store, handing a customer their files, or keeping a copy when an account
closes. It isn't a replacement for database backups:

- an export taken while the application runs isn't a snapshot of one moment;
- it covers one container, not the store;
- it can't restore to "five minutes before the mistake".

Use database backups for recovery, and exports for moving and archiving.

## Expiry and restores

A restored database contains files that may have expired since the backup was taken. If
[expiry](expiry.md) is on, the next sweep deletes them, so look at a restored backup with expiry off if you need
those files.
