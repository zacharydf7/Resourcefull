using Microsoft.AspNetCore.Http;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull.Http;

internal class HttpContextUriProvider : IUriProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextUriProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetAbsoluteUri(string? identifier)
    {
        HttpRequest request = _httpContextAccessor.HttpContext.Request;
        var scheme = request.Scheme;
        var host = request.Host.ToUriComponent();
        var pathBase = request.PathBase.ToUriComponent();
        PathString path = request.Path;
        return $"{scheme}://{host}{pathBase}{path}/{identifier}";
    }
}