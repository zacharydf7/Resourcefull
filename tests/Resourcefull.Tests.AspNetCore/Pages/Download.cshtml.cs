using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore.Pages;

/// <summary>
/// A Razor page whose handler returns the result itself, declared as its own type: Razor Pages refuses a handler whose
/// return type isn't an action result.
/// </summary>
public class DownloadModel(IResourceServiceClient resources) : PageModel
{
    public ResourceLookupHttpResult OnGet(string name) => TypedResults.Resource(resources.GetResourceContainerClient(TestApp.Container), name);
}
