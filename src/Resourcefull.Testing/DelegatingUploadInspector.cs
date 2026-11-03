using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

/// <summary>An upload inspector that answers as the delegate registered with it does.</summary>
internal sealed class DelegatingUploadInspector(
    Func<ResourceUploadInspection, CancellationToken, Task<ResourceInspectionResult>> answer) : IResourceUploadInspector
{
    public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload, CancellationToken cancellationToken) =>
        await answer(upload, cancellationToken);

    /// <summary>Registers the inspector, answering as <paramref name="answer"/> does.</summary>
    public static void Register(ResourcefullOptionsBuilder options,
        Func<ResourceUploadInspection, CancellationToken, Task<ResourceInspectionResult>> answer)
    {
        options.UseUploadInspector<DelegatingUploadInspector>();
        options.Services.AddSingleton(answer);
    }
}
