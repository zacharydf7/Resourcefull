using Resourcefull;

namespace BlazorTestClient.Utilities;

public class TestUriProvider(IConfiguration configuration) : IResourceUriProvider
{
    private readonly string? _baseUrl = configuration["BaseUrl"];

    public string? GetAbsoluteUri(ResourceUriContext context)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return null;

        if (!string.IsNullOrEmpty(context.ResourceName))
        {
            return $"{_baseUrl.Trim('/')}/resources/{Uri.EscapeDataString(context.ContainerName)}/" +
                $"?resourceName={Uri.EscapeDataString(context.ResourceName)}&versionId={Uri.EscapeDataString(context.VersionId ?? "")}";
        }
        else
        {
            return $"{_baseUrl.Trim('/')}/containers/{Uri.EscapeDataString(context.ContainerName)}";
        }
    }
}
