# How SQL Server stores files

What the SQL Server provider does with your files, and what that means for performance and the database's
size.

## How content is stored

Content is stored in chunks of up to 64 KB. An upload inserts them in one transaction, so a version isn't visible until
all of its content is stored, and the name is locked only at the end, to make the new version current. A download
reads a few chunks at a time as its stream is read, so memory use stays flat whatever the file size.

## Performance

- **Memory doesn't grow with file size.** An upload or a download holds about 1 MiB of content at a time (16 chunks),
  in buffers it reuses, whatever the file's size. Eight uploads at once hold about 8 MiB of content between them.
- **About one round trip per MiB.** An upload inserts 16 chunks per round trip and a download reads 16 per query, plus
  a few round trips per operation to find the version, lock the name and make the version current. Reading a
  resource's attributes, listing a container and listing a name's versions are one query each.
- **Copies and moves stay in the database.** A copy is one `INSERT … SELECT` of the source's chunks, so none of the
  content goes through the application: copying 16 MiB takes about half the round trips of uploading it and a
  fraction of the memory. A move changes only the versions' container and name, whatever their size.
- **A range costs what its own bytes cost.** A [range download](../downloading.md#downloading-part-of-a-resource)
  finds its first chunk from the chunks' lengths, without reading content, and reads only the chunks the range
  covers, so the last MiB of a 64 MiB file costs about what a 1 MiB file does.
- **Throughput is the server's.** With the client and SQL Server on the same machine, uploads of large files run at
  roughly 60 to 120 MiB/s and downloads at 150 to 375 MiB/s. On a network, latency and bandwidth set the pace, and
  the round trips above are what latency multiplies.
- **The transaction log takes about as much as the content.** An upload writes about 1.13 times its size to the log,
  and deleting a version about 1.07 times its size, since SQL Server logs the content it frees. Size the log for the
  largest uploads and deletes you expect to run at once, and back it up regularly under the full recovery model.
- **Concurrent writers don't wait for each other's content.** Uploads to different names run fully in parallel, and
  uploads to the same name wait for each other only while making their versions current.

These figures come from the performance tests the repository runs on every change (see
[CONTRIBUTING.md](../../CONTRIBUTING.md#performance-tests)), which fail the build when an operation gets more expensive.
There's nothing to tune: the chunk and page sizes are fixed.
