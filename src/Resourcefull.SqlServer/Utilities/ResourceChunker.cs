using System.Runtime.CompilerServices;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.Utilities;

internal class ResourceChunker
{
    private static readonly int _chunkSize = 65535; // The maximum amount of data that can be transmitted over a network in a single TCP packet

    /// <summary>
    /// Reads the stream one chunk at a time, so the caller can store each before the next is read. Every chunk but the
    /// last is full, even from a stream that returns short reads.
    /// </summary>
    public static async IAsyncEnumerable<ResourceChunkModel> ChunksAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        var buffer = new byte[_chunkSize];

        while (true)
        {
            int bytesRead = await stream.ReadAtLeastAsync(buffer, _chunkSize, throwOnEndOfStream: false, cancellationToken);
            if (bytesRead == 0) yield break;

            yield return new ResourceChunkModel
            {
                Index = ++index,
                Data = buffer.AsSpan(0, bytesRead).ToArray()
            };

            if (bytesRead < _chunkSize) yield break;
        }
    }
}
