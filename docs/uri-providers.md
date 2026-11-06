# URI providers

Resourcefull doesn't serve files itself; your application does. A URI provider lets each `ContainerInfo` and
`ResourceInfo` carry the URI your application serves it at, so callers get a link along with the attributes.

Implement `IResourceUriProvider`. `GetAbsoluteUri` gets a `ResourceUriContext` with the container name and, for a
resource, its name and version ID, and returns the URI, or `null` for none:

```csharp
public class FilesUriProvider(IConfiguration configuration) : IResourceUriProvider
{
    private readonly string? _baseUrl = configuration["Files:BaseUrl"]?.TrimEnd('/');

    public string? GetAbsoluteUri(ResourceUriContext context)
    {
        if (_baseUrl is null) return null;

        string container = Uri.EscapeDataString(context.ContainerName);
        if (context.ResourceName is null) return $"{_baseUrl}/files/{container}";

        string uri = $"{_baseUrl}/files/{container}?name={Uri.EscapeDataString(context.ResourceName)}";
        return context.VersionId is null ? uri : $"{uri}&version={Uri.EscapeDataString(context.VersionId)}";
    }
}
```

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseUriProvider<FilesUriProvider>());
```

- **Escape every part.** Names can contain `/`, `?`, `#`, `&`, spaces and any other character, so pass each part
  through `Uri.EscapeDataString`.
- **The provider is a scoped service**, so it can depend on configuration or other services, but not on the current
  request unless you inject `IHttpContextAccessor`.
- **URIs are made whenever they're returned.** Every container and version the clients return gets its URI from the
  provider at that moment, so changing the provider or its base URL applies to everything already stored. Without a
  provider, `Uri` is `null`.
