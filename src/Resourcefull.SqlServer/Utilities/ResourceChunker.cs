using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.Utilities;

internal class ResourceChunker
{
    private static readonly int _chunkSize = 65535; // The maximum amount of data that can be transmitted over a network in a single TCP packet

    public static async Task<List<ResourceChunkModel>> ChunksAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek) stream.Position = 0;

        var chunks = new List<ResourceChunkModel>();
        var index = 0;
        var buffer = new byte[_chunkSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0) break;

            ++index;

            chunks.Add(new ResourceChunkModel
            {
                Index = index,
                Data = buffer.Take(bytesRead).ToArray()
            });
        }

        return chunks;
    }
}
