# URI providers

Resourcefull doesn't serve files itself; your application does. A URI provider lets each `ResourceContainerInfo` and
`ResourceInfo` carry the URI your application serves it at, so callers get a link along with the attributes.

Implement `IUriProvider`. `GetAbsoluteUri` gets a `ResourcefullContext` with the container name and, for a resource,
its name and version ID, and returns the URI, or `null` for none:

```csharp
public class FilesUriProvider(IConfiguration configuration) : IUriProvider
{
    private readonly string? _baseUrl = configuration["Files:BaseUrl"]?.TrimEnd('/');

    public string? GetAbsoluteUri(ResourcefullContext context)
    {
        if (_baseUrl is null) return null;

        string container = Uri.EscapeDataString(context.ContainerName);
        if (context.ResourceName is null) return $"{_baseUrl}/files/{container}";

        string uri = $"{_baseUrl}/files/{container}?name={Uri.EscapeDataString(context.ResourceName)}";
        return context.ResourceVersionId is null ? uri : $"{uri}&version={Uri.EscapeDataString(context.ResourceVersionId)}";
    }
}
```

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .SetUriProvider<FilesUriProvider>());
```

- **Escape every part.** Names can contain `/`, `?`, `#`, `&`, spaces and any other character, so pass each part
  through `Uri.EscapeDataString`.
- **The provider is a scoped service**, so it can depend on configuration or other services, but not on the current
  request unless you inject `IHttpContextAccessor`.
- **URIs are stored when they're made.** A container gets its URI when it's created, and a version when it's
  uploaded, and reads return the URI stored with it. Changing the provider or its base URL therefore changes only what's
  created afterwards. (`GetResourceContainerClient` asks the provider each time, so its client's URI is always
  current.)
