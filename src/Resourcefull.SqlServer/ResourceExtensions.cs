using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer;
internal static class ResourceExtensions
{
    public static ResourceInfo ToResourceInfo(this ResourceModel resource)
    {
        return new ResourceInfo
        {
            ContainerName = resource.ContainerName,
            Name = resource.Name,
            ContentType = resource.ContentType,
            ContentLength = resource.ContentLength,
            Uri = resource.Uri,
            Metadata = resource.Metadata
        };
    }
}
