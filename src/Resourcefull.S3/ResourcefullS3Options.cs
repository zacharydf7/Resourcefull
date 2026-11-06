using Amazon.S3;

namespace Resourcefull.S3;

/// <summary>
/// How Resourcefull reaches an S3-compatible store.
/// </summary>
public sealed class ResourcefullS3Options
{
    /// <summary>
    /// The client to reach the store with. When <see langword="null"/>, the <see cref="IAmazonS3"/> registered in the
    /// application's services is used, or, without one, an <see cref="AmazonS3Client"/> configured from the
    /// environment, as the AWS SDK configures one by default.
    /// </summary>
    public IAmazonS3? Client { get; set; }

    /// <summary>
    /// What every key Resourcefull writes starts with, so it can share a bucket: <c>resourcefull/</c> by default. Empty
    /// keeps everything at the bucket's top level.
    /// </summary>
    public string KeyPrefix { get; set; } = "resourcefull/";
}
