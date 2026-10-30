# When to use Resourcefull

Where an application keeps the files its users upload is easy to decide early and expensive to change later. This
page compares the three usual places, your database, an object storage service and the server's disk, and says where
Resourcefull fits and where something else serves you better.

## The three places files go

- **In your database.** The files sit next to the records that mention them. One backup holds both, so a restore can't
  bring back an invoice row without its PDF, and the same access rules, network and monitoring cover both. There's no
  other service to set up, secure or pay for. In exchange, database storage costs more per gigabyte than the other
  two, and the database's backups, restores and copies grow with the files.
- **In an object storage service.** A service built for files: storage is cheap per gigabyte, there's practically no
  limit on total size, and browsers and content delivery networks can fetch files from it directly. In exchange, it's
  another service with its own credentials, SDK and bill, and its backups are separate from your database's, so after
  a restore the two can disagree about which files exist.
- **On the server's disk.** The simplest to start with. It stops being simple once the application runs on more than
  one server, since each sees only its own disk unless you add a shared one, and versions, hashes, quotas and safe
  concurrent writes are yours to build.

## Where Resourcefull fits

Resourcefull stores files in your database, with the SQL Server provider, and gives them the features the other
approaches leave to you: [versions](versioning.md), content hashes, [quotas and allowed file types](container-policies.md),
[expiry](expiry.md), [signed links](sharing-links.md) and [ASP.NET Core endpoints](aspnet-core/index.md) that answer
with the right headers. Quotas, file types, expiry and signed links are new in 3.2 <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>.

It suits you when:

- **The files are part of your application's data,** such as invoices, contracts, reports or profile pictures, and
  you want them backed up, restored and secured along with it.
- **The total size is one your database can comfortably hold, back up and restore** in the time you can afford:
  typically gigabytes to a few hundred gigabytes, not many terabytes.
- **You can't, or would rather not, add an object storage service,** for example on your own servers, or where every
  copy of the data has to stay in one place you control.
- **You want versions, hashes, quotas and expiry** without writing them.

Something else serves you better when:

- **There's a lot of data, and it grows fast.** At many terabytes, database storage costs and backup times dominate,
  and object storage is cheaper.
- **Files go to a large public audience,** such as a site's images or video. Every Resourcefull download passes
  through your application and your database, while a content delivery network in front of object storage serves
  files without either.
- **Many large uploads arrive at once.** An upload holds a database connection for as long as the client takes to
  send the file (see [Uploads, connections and the log](providers/sql-server-operations.md#uploads-connections-and-the-log)),
  so hundreds of simultaneous slow uploads need a pool, and a server, sized for them.
- **Other systems read the files directly,** outside .NET. They'd have to go through an API you build, since
  Resourcefull's tables are its own to change.

## At a glance

| | Your database, with Resourcefull | Object storage service | Server's disk |
|---|---|---|---|
| Cost per gigabyte | Highest | Lowest | Low |
| Backups | One backup with your data | Separate from your data | Separate from your data |
| Several application servers | Works | Works | Needs a shared disk |
| Versions, hashes, quotas, expiry | Built in | Some, depending on the service | Yours to build |
| Serving many readers | Through your application | Directly, or through a content delivery network | Through your application or web server |
| Another service to run | No | Yes | No |

## Changing your mind later

Your code talks to `IResourceServiceClient`, not to SQL Server, so moving the bytes elsewhere later means changing the
provider, not the code that uses it. A [provider of your own](providers/writing-a-provider.md) can store them anywhere,
and [exporting and importing](backup-and-move.md) <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>
moves containers between stores.
