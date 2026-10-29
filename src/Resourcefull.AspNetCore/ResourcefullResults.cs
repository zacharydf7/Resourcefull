using Microsoft.AspNetCore.Http;

namespace Resourcefull.AspNetCore;

/// <summary>Adds <c>Results.Resource</c>, which sends a download as a <see cref="ResourceHttpResult"/>.</summary>
public static class ResourcefullResults
{
    extension(Results)
    {
        /// <summary>
        /// Sends a download with its headers, answering conditional and <c>HEAD</c> requests. See
        /// <see cref="ResourceHttpResult"/>.
        /// </summary>
        /// <param name="download">What <see cref="IResourceContainerClient.DownloadStreamingAsync(string, string?, CancellationToken)"/> returned.</param>
        /// <param name="options">How to present it; <see langword="null"/> offers it as a download under its name.</param>
        /// <returns>The result, which disposes the download stream once it's sent.</returns>
        public static IResult Resource(ResourceDownloadResult download, ResourceResultOptions? options = null) =>
            new ResourceHttpResult(download, options);
    }
}

/// <summary>Adds <c>TypedResults.Resource</c>, which sends a download as a <see cref="ResourceHttpResult"/>.</summary>
public static class ResourcefullTypedResults
{
    extension(TypedResults)
    {
        /// <inheritdoc cref="ResourcefullResults.Resource(ResourceDownloadResult, ResourceResultOptions?)"/>
        public static ResourceHttpResult Resource(ResourceDownloadResult download, ResourceResultOptions? options = null) =>
            new(download, options);
    }
}
