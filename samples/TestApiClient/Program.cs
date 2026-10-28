using Microsoft.AspNetCore.Mvc;
using Resourcefull;
using Resourcefull.SqlServer;
using System.Net.Mime;
using System.Text.Json;
using TestApiClient;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddResourcefull(options =>
{
    options.UseSqlServer("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=ResourceDB;Integrated Security=True;Connect Timeout=30;Encrypt=False;Trust Server Certificate=True;Application Intent=ReadWrite;Multi Subnet Failover=False");
    //options.UseInMemory();     //uncomment and comment out the line above to use the InMemory Database
    options.SetUriProvider<TestUriProvider>();
    //options.UseVersioning();
});

WebApplication app = builder.Build();

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
        return Results.Problem("Resource name cannot be null or empty");
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

        return Results.Stream(downloadResult.Stream!, downloadResult.ResourceInfo.ContentType);
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
    if (!await resourceServiceClient.ContainerExistsAsync(containerName)) return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    var resourceUploadOptions = new ResourceUploadOptions()
    {
        Overwrite = overwrite,
        Headers = new(file.ContentType, file.Length),
        Metadata = string.IsNullOrEmpty(metadataJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)!
    };

    try
    {
        ResourceInfo resourceInfo = await containerClient.UploadAsync(file.FileName, file.OpenReadStream(), resourceUploadOptions);
        return Results.Created(resourceInfo.Uri, resourceInfo);
    }
    catch (ResourceConflictException)
    {
        return Results.Problem($"Resource with name '{file.FileName}' already exists in container '{containerName}'. Set 'overwrite' to true to replace the existing resource.");
    }
}).DisableAntiforgery();

app.MapPost("/api/containers/{containerName}", async (string containerName, IResourceServiceClient resourceServiceClient,
    [FromForm] string? metadataJson = null) =>
{
    var metadata = string.IsNullOrEmpty(metadataJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)!;

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
        return Results.Problem("Resource name cannot be null or empty");
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
    string resourceName, string versionId, IResourceServiceClient resourceServiceClient) =>
{
    if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(versionId))
    {
        return Results.Problem("Resource name and versionId cannot be null or empty");
    }

    if (!await resourceServiceClient.ContainerExistsAsync(containerName))
        return Results.NotFound("Resource Container not found.");

    IResourceContainerClient containerClient = resourceServiceClient.GetResourceContainerClient(containerName);

    try
    {
        ResourceInfo? restored = await containerClient.RestoreVersionAsync(resourceName, versionId);
        return restored is null
            ? Results.NotFound($"Resource '{resourceName}' with version '{versionId}' not found.")
            : Results.Ok(restored);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
