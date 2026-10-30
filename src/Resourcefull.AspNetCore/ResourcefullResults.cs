using Microsoft.AspNetCore.Http;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Adds <c>Results.Resource</c>, which sends a download as a <see cref="ResourceHttpResult"/>, or looks a resource up
/// and sends it, ranges included, as a <see cref="ResourceLookupHttpResult"/>.
/// </summary>
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

        /// <summary>
        /// Looks up a resource and sends it with its headers, answering conditional, <c>HEAD</c> and range requests,
        /// with 404 when it doesn't exist. See <see cref="ResourceLookupHttpResult"/>.
        /// </summary>
        /// <param name="container">The container the resource is in.</param>
        /// <param name="resourceName">The name of the resource to send.</param>
        /// <param name="versionId">The version to send; <see langword="null"/> sends the current one.</param>
        /// <param name="options">How to present it; <see langword="null"/> offers it as a download under its name.</param>
        /// <returns>The result, which opens the download when it runs and disposes it once it's sent.</returns>
        public static IResult Resource(IResourceContainerClient container, string resourceName, string? versionId = null,
            ResourceResultOptions? options = null) =>
            new ResourceLookupHttpResult(container, resourceName, options, versionId);

        /// <summary>
        /// Sends the resource a signed link grants, to whoever holds the link, without authentication. See
        /// <see cref="SharedResourceHttpResult"/>, and <see cref="IResourceLinkSigner.CreateTokenAsync"/> to make links.
        /// </summary>
        /// <param name="resources">The client to read the resource through.</param>
        /// <param name="token">The token from the link's URL.</param>
        /// <returns>The result, which checks the token when it runs.</returns>
        public static IResult SharedResource(IResourceServiceClient resources, string token) =>
            new SharedResourceHttpResult(resources, token);
    }
}

/// <summary>
/// Adds <c>TypedResults.Resource</c>, which sends a download as a <see cref="ResourceHttpResult"/>, or looks a resource
/// up and sends it, ranges included, as a <see cref="ResourceLookupHttpResult"/>.
/// </summary>
public static class ResourcefullTypedResults
{
    extension(TypedResults)
    {
        /// <inheritdoc cref="ResourcefullResults.Resource(ResourceDownloadResult, ResourceResultOptions?)"/>
        public static ResourceHttpResult Resource(ResourceDownloadResult download, ResourceResultOptions? options = null) =>
            new(download, options);

        /// <inheritdoc cref="ResourcefullResults.Resource(IResourceContainerClient, string, string?, ResourceResultOptions?)"/>
        public static ResourceLookupHttpResult Resource(IResourceContainerClient container, string resourceName,
            string? versionId = null, ResourceResultOptions? options = null) =>
            new(container, resourceName, options, versionId);

        /// <inheritdoc cref="ResourcefullResults.SharedResource(IResourceServiceClient, string)"/>
        public static SharedResourceHttpResult SharedResource(IResourceServiceClient resources, string token) =>
            new(resources, token);
    }
}
