using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Resourcefull;
using Resourcefull.InMemory;
using Resourcefull.SqlServer;
using System.Net.Mime;
using System.Text.Json;
using TestApiClient;

const string InvalidMetadata = "metadataJson must be a JSON object whose values are strings, such as {\"author\":\"me\"}.";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Resources are kept in memory unless a SQL Server connection string is configured (see the samples README).
string? connectionString = builder.Configuration.GetConnectionString("Resources");
builder.Services.AddResourcefull(options =>
{
    if (string.IsNullOrWhiteSpace(connectionString)) options.UseInMemory();
    else options.UseSqlServer(connectionString);
    options.SetUriProvider<TestUriProvider>();
    //options.UseVersioning();
});

WebApplication app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    await app.Services.MigrateResourcefullDatabaseAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/api/containers", async (IResourceServiceClient resourceServiceClient) =>
{
    IReadOnlyList<ResourceContainerInfo> containers = await resourceServiceClient.GetResourceContainersAsync();
    return Results.Ok(containers);
});

app.MapGet("/api/containers/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient) =>
{
    ResourceContainerInfo? containerInfo = await resourceServiceClient.GetResourceContainerInfoAsync(containerName);
    if (containerInfo is null)
    {
        return Results.NotFound("Resource Container not found.");
    }

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);
    IReadOnlyList<ResourceInfo> containerResourceInfo = await containerClient.GetResourcesAsync();

    return Results.Ok(new { containerInfo.Name, containerInfo.Metadata, containerResourceInfo });
});

app.MapGet("/api/resources/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient,
    HttpResponse response, string resourceName, bool stream = false, bool isAttachment = false, string? versionId = null) =>
{
    if (string.IsNullOrEmpty(resourceName))
    {
        return Results.BadRequest("Resource name cannot be null or empty");
    }

    if (!await resourceServiceClient.ContainerExistsAsync(containerName)) return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    if (stream)
    {
        ResourceDownloadResult? downloadResult = await containerClient.DownloadStreamingAsync(resourceName, versionId);
        if (downloadResult is null) return Results.NotFound("Resource not found.");

        var contentDisposition = new ContentDisposition()
        {
            FileName = downloadResult.ResourceInfo.Name,
            Inline = !isAttachment
        };

        response.Headers.ContentDisposition = contentDisposition.ToString();

        return Results.Stream(downloadResult.Stream, downloadResult.ResourceInfo.ContentType);
    }
    else
    {
        ResourceInfo? resourceInfo = await containerClient.GetResourceInfoAsync(resourceName, versionId);
        if (resourceInfo is null) return Results.NotFound("Resource not found.");

        return Results.Ok(resourceInfo);
    }
});

app.MapPost("/api/resources/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient, IFormFile file,
    [FromForm] string? metadataJson = null, bool overwrite = false) =>
{
    if (!TryReadMetadata(metadataJson, out Dictionary<string, string> metadata)) return Results.BadRequest(InvalidMetadata);
    if (!await resourceServiceClient.ContainerExistsAsync(containerName)) return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    var resourceUploadOptions = new ResourceUploadOptions()
    {
        Overwrite = overwrite,
        Headers = new(file.ContentType, file.Length),
        Metadata = metadata
    };

    try
    {
        ResourceInfo resourceInfo = await containerClient.UploadAsync(file.FileName, file.OpenReadStream(), resourceUploadOptions);
        return Results.Created(resourceInfo.Uri, resourceInfo);
    }
    catch (ResourceConflictException)
    {
        return Results.Conflict($"Resource with name '{file.FileName}' already exists in container '{containerName}'. Set 'overwrite' to true to replace the existing resource.");
    }
}).DisableAntiforgery();

app.MapPost("/api/containers/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient,
    [FromForm] string? metadataJson = null) =>
{
    if (!TryReadMetadata(metadataJson, out Dictionary<string, string> metadata)) return Results.BadRequest(InvalidMetadata);

    try
    {
        IResourceContainerClient containerClient = await resourceServiceClient.CreateResourceContainerAsync(containerName, metadata);
        ResourceContainerInfo containerInfo = containerClient.ResourceContainerInfo;
        return Results.Created(containerInfo.Uri, containerInfo);
    }
    catch (ResourceConflictException)
    {
        return Results.Conflict($"Resource Container with name {containerName} already exists.");
    }
}).DisableAntiforgery();

app.MapDelete("/api/resources/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient, string resourceName) =>
{
    if (string.IsNullOrEmpty(resourceName))
    {
        return Results.BadRequest("Resource name cannot be null or empty");
    }

    if (!await resourceServiceClient.ContainerExistsAsync(containerName)) return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    bool deleted = await containerClient.DeleteResourceIfExistsAsync(resourceName);
    return deleted ? Results.NoContent() : Results.NotFound();
}).DisableAntiforgery();

app.MapDelete("/api/containers/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient) =>
{
    bool deleted = await resourceServiceClient.DeleteResourceContainerIfExistsAsync(containerName);
    return deleted ? Results.NoContent() : Results.NotFound();

}).DisableAntiforgery();

app.MapPost("/api/resources/{containerName}/restore", async (string containerName,
    string resourceName, string versionId, IResourceServiceClient resourceServiceClient, IOptions<ResourcefullOptions> options) =>
{
    if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(versionId))
    {
        return Results.BadRequest("Resource name and versionId cannot be null or empty");
    }

    if (!options.Value.IsVersioningEnabled)
        return Results.BadRequest("Versioning isn't enabled, so there are no versions to restore.");

    if (!await resourceServiceClient.ContainerExistsAsync(containerName))
        return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    ResourceInfo? restored = await containerClient.RestoreVersionAsync(resourceName, versionId);
    return restored is null
        ? Results.NotFound($"Resource '{resourceName}' with version '{versionId}' not found.")
        : Results.Ok(restored);
});

app.Run();

static bool TryReadMetadata(string? metadataJson, out Dictionary<string, string> metadata)
{
    metadata = [];
    if (string.IsNullOrEmpty(metadataJson)) return true;
    try
    {
        metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson) ?? [];
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}
