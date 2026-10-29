using System.Runtime.CompilerServices;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.Utilities;

internal class ResourceChunker
{
    /// <summary>The size of every chunk but a version's last.</summary>
    public const int ChunkSize = 65535;

    /// <summary>
    /// Reads the stream one chunk at a time, so the caller can store each before the next is read. Every chunk but the
    /// last is full, even from a stream that returns short reads.
    /// </summary>
    /// <param name="stream">The content.</param>
    /// <param name="reuseAfter">
    /// How many buffers full chunks share in turn, so a chunk's data is overwritten that many chunks later: the caller
    /// must be done with each chunk by then. A short final chunk gets an array of its own, sized to its data.
    /// </param>
    /// <param name="cancellationToken">Cancels reading.</param>
    public static async IAsyncEnumerable<ResourceChunkModel> ChunksAsync(Stream stream, int reuseAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        var buffers = new byte[reuseAfter][];

        while (true)
        {
            byte[] buffer = buffers[index % buffers.Length] ??= new byte[ChunkSize];
            int bytesRead = await stream.ReadAtLeastAsync(buffer, ChunkSize, throwOnEndOfStream: false, cancellationToken);
            if (bytesRead == 0) yield break;

            bool last = bytesRead < ChunkSize;
            byte[] data = last ? buffer[..bytesRead] : buffer;

            yield return new ResourceChunkModel { Index = ++index, Data = data };

            if (last) yield break;
        }
    }
}
