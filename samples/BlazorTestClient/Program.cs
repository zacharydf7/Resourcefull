using BlazorTestClient.Components;
using BlazorTestClient.Utilities;
using Microsoft.AspNetCore.StaticFiles;
using Resourcefull;
using Resourcefull.SqlServer;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".snz"] = "application/project-file";

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddResourcefull(options =>
{
    options.UseSqlServer("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=ResourceDB;Integrated Security=True;Connect Timeout=30;Encrypt=False;Trust Server Certificate=True;Application Intent=ReadWrite;Multi Subnet Failover=False");
    //options.UseInMemory();
    options.SetUriProvider<TestUriProvider>();
    //options.UseVersioning();
});
builder.Services.AddSingleton(provider);

WebApplication app = builder.Build();

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

app.Run();