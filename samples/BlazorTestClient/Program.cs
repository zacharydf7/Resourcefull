using BlazorTestClient.Components;
using BlazorTestClient.Utilities;
using Microsoft.AspNetCore.StaticFiles;
using Resourcefull;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;
using Resourcefull.SqlServer;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

var provider = new FileExtensionContentTypeProvider();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Resources are kept in memory unless a SQL Server connection string is configured (see the samples README).
string? connectionString = builder.Configuration.GetConnectionString("Resources");
builder.Services.AddResourcefull(options =>
{
    if (string.IsNullOrWhiteSpace(connectionString)) options.UseInMemory();
    else options.UseSqlServer(connectionString);
    options.SetUriProvider<TestUriProvider>();
    //options.UseVersioning();
});
builder.Services.AddSingleton(provider);

WebApplication app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    await app.Services.MigrateResourcefullDatabaseAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The page links here to download, so a file streams from storage to the browser instead of being read into memory
// and sent through the page's connection. Like the rest of this sample, it's open to anyone: see the docs' "Securing
// file endpoints" before using it elsewhere.
app.MapMethods("/download/{container}/{**name}", ["GET", "HEAD"], (string container, string name, string? versionId,
    IResourceServiceClient resources) =>
    Results.Resource(resources.GetResourceContainerClient(container), name, versionId));

app.Run();