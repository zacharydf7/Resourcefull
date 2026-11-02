# Files and your own data

Most applications keep their own record of each file: an invoice row with the name of its PDF, or a document that
points at one version of a file. A file and its row are two separate writes. Resourcefull stores files through its own
connections and transactions, so it can't take part in a transaction of yours, even when its tables are in your
database. If your application stops between the two writes, they can disagree.

There are two ways they can disagree:

- **A row that points at a file that isn't there.** This one breaks your application: the invoice page has nothing to
  show.
- **A file that no row points at,** an *orphan*. It's harmless, apart from the storage it takes, and easy to find and
  delete later.

This page shows the order of steps that makes sure only the second can happen, and how to clean up after it.

## Name each file once

Give each file a name of its own, such as one built from your row's ID, and upload it with `CreateOnly`, so no other
upload can replace it:

```csharp
ResourceInfo stored = await invoices.UploadAsync($"{invoice.Id}.pdf", file, new ResourceUploadOptions
{
    Headers = new ResourceHeaders("application/pdf", null),
    CreateOnly = true
});
```

Record the name in your row, and its `VersionId` too if you want to be sure the file is the one you saved. Reusing a
name would let one row's upload replace another row's file. With [versioning](versioning.md), the first row's file
stays as an older version until it's pruned, but a row that recorded only the name now gets the new one; without
versioning, the first row's file is gone.

## Saving a new file

Store the file first, then save your row:

```csharp
ResourceInfo stored = await invoices.UploadAsync($"{invoice.Id}.pdf", file,
    new ResourceUploadOptions { CreateOnly = true });

invoice.PdfName = stored.Name;
invoice.PdfVersionId = stored.VersionId;
db.Invoices.Add(invoice);
await db.SaveChangesAsync(cancellationToken);
```

The row is saved only once the file is there, so it never points at a missing file. If the application stops, or the
save fails, after the upload, the file is an orphan, and the [sweep](#deleting-orphans) below deletes it. Don't delete
it as soon as the save throws: a save can fail after your database has committed it, such as when the connection drops
before the reply arrives, and the delete would then remove a file the row points at.

It's tempting to upload the file with an [expiry](expiry.md) and clear it once the row is saved, so a file whose row
never arrives deletes itself. Don't: if the application stops after saving the row but before clearing the expiry, the
file expires while the row still points at it, which is the kind of disagreement this page avoids.

## Replacing a file

Store the new file under a new name, point the row at it, and only then delete the old one:

```csharp
string oldName = invoice.PdfName;
ResourceInfo stored = await invoices.UploadAsync($"{invoice.Id}-{Guid.NewGuid():N}.pdf", file,
    new ResourceUploadOptions { CreateOnly = true });

invoice.PdfName = stored.Name;
invoice.PdfVersionId = stored.VersionId;
await db.SaveChangesAsync(cancellationToken);

await invoices.DeleteResourceIfExistsAsync(oldName, cancellationToken: cancellationToken);
```

At every step, the row points at a file that exists. If the application stops partway, either the new file or the
old one is left as an orphan.

## Deleting

Delete the row first, then the file:

```csharp
db.Invoices.Remove(invoice);
await db.SaveChangesAsync(cancellationToken);
await invoices.DeleteResourceIfExistsAsync(invoice.PdfName, cancellationToken: cancellationToken);
```

The other way around, a failure after the file's delete would leave the row pointing at nothing.

## Deleting orphans

Every order above can leave a file that no row points at. A job that runs regularly, such as once a day, finds those
files and deletes them:

```csharp
// Skip recent files: an upload whose row is still being saved isn't an orphan yet.
DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-1);
await foreach (ResourceInfo file in invoices.ListResourcesAsync(cancellationToken: cancellationToken))
{
    if (file.LastModified > cutoff) continue;
    bool pointedAt = await db.Invoices.AnyAsync(i => i.PdfName == file.Name, cancellationToken);
    if (!pointedAt) await invoices.DeleteResourceIfExistsAsync(file.Name, file.VersionId, cancellationToken: cancellationToken);
}
```

- **Leave a margin.** The hour should be longer than any upload and row save take together, so the job never deletes a
  file whose row is about to be saved.
- **Delete by version ID.** Passing the version the job looked at means it never deletes a newer upload to the same
  name.
- **For a large container,** check a page of names against your table at a time, rather than one query per file.
  [Listing resources](listing.md) shows how to page.

## Reading

Even with this order, a row can outlive its file: someone deleted the file by hand, or you restored your database and
Resourcefull's to different moments. Treat a missing file as an expected case rather than a crash:

```csharp
ResourceDownloadResult? download = await invoices.DownloadStreamingAsync(invoice.PdfName, invoice.PdfVersionId);
if (download is null) return Results.NotFound();
```

## Backups

Restoring your data and the files from backups taken at different moments can also make them disagree. Keeping
Resourcefull's tables in your application's database means one backup holds both as they were at the same moment.
See [Keep it consistent with your own data](backups-and-recovery.md#keep-it-consistent-with-your-own-data).
