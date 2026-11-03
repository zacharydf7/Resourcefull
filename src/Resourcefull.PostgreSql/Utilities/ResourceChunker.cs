using System.Runtime.CompilerServices;
using Resourcefull.PostgreSql.Models;

namespace Resourcefull.PostgreSql.Utilities;

internal class ResourceChunker
{
    /// <summary>
    /// The size of every chunk but a version's last, about 40 KB, the same as the SQL Server provider's. PostgreSQL stores
    /// a value this large out of line (TOAST), in pieces of about 2 KB, so the size doesn't need to fit its 8 KB pages. A
    /// page of a read, about 1 MiB, holds some 26 chunks.
    /// </summary>
    public const int ChunkSize = 40_200;

    /// <summary>
    /// Reads the stream one chunk at a time, so the caller can store each before the next is read. Every chunk but the
    /// last is full, even from a stream that returns short reads.
    /// </summary>
    /// <param name="stream">The content.</param>
    /// <param name="reuseAfter">
    /// How many buffers full chunks share in turn, so a chunk's data is overwritten that many chunks later: the caller
    /// must be done with each chunk by then. A short final chunk gets an array of its own, sized to its data.
    /// </param>
    /// <param name="afterIndex">The index the first chunk follows: that of the last chunk already stored, for an append.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    public static async IAsyncEnumerable<ResourceChunkModel> ChunksAsync(Stream stream, int reuseAfter, int afterIndex = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = afterIndex;
        var buffers = new byte[reuseAfter][];

        while (true)
        {
            byte[] buffer = buffers[index % buffers.Length] ??= new byte[ChunkSize];
            int bytesRead = await stream.ReadAtLeastAsync(buffer, ChunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) yield break;

            bool last = bytesRead < ChunkSize;
            byte[] data = last ? buffer[..bytesRead] : buffer;

            yield return new ResourceChunkModel { Index = ++index, Data = data };

            if (last) yield break;
        }
    }
}
