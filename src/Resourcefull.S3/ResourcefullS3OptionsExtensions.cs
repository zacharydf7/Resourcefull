using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.S3;
using Resourcefull.ObjectStorage;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull to store resources in an S3-compatible store.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullS3OptionsExtensions
{
    /// <summary>
    /// The longest key prefix. S3 allows a key 1,024 bytes; an escaped name takes up to 773 of them, and the
    /// container's ID, the separators and a metadata search entry's token another 60.
    /// </summary>
    internal const int MaxKeyPrefixBytes = 190;

    /// <summary>
    /// Stores resources in <paramref name="bucketName"/>, which must exist, in Amazon S3 or another store that speaks
    /// its API. The store must honor conditional writes, <c>If-None-Match: *</c> and <c>If-Match</c> on
    /// <c>PutObject</c>, which Resourcefull relies on to keep one current version per name.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="bucketName">The bucket that holds Resourcefull's objects.</param>
    /// <param name="configure">Sets the client and the key prefix; <see langword="null"/> keeps the defaults.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="bucketName"/> is null, empty or whitespace (<see cref="ArgumentNullException"/> when null), or
    /// the key prefix is null or longer than 190 bytes of UTF-8.
    /// </exception>
    public static ResourcefullOptionsBuilder UseS3(this ResourcefullOptionsBuilder builder, string bucketName,
        Action<ResourcefullS3Options>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        var options = new ResourcefullS3Options();
        configure?.Invoke(options);
        ArgumentNullException.ThrowIfNull(options.KeyPrefix, nameof(configure));
        if (System.Text.Encoding.UTF8.GetByteCount(options.KeyPrefix) > MaxKeyPrefixBytes)
            throw new ArgumentException($"The key prefix can be at most {MaxKeyPrefixBytes} bytes of UTF-8.", nameof(configure));

        string? storeName = builder.StoreName;
        // The repository holds no state of its own, and the client is thread-safe, so one instance serves every scope.
        builder.UseProvider(services => new ObjectStoreRepository(
            new S3ObjectStore(options.Client ?? services.GetService<IAmazonS3>() ?? new AmazonS3Client(), bucketName),
            options.KeyPrefix, services.GetService<TimeProvider>(), storeName, services.SearchableMetadataKeys(storeName)),
            ServiceLifetime.Singleton);
        return builder;
    }
}
