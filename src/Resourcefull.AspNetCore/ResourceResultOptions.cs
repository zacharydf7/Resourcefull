namespace Resourcefull.AspNetCore;

/// <summary>
/// How <see cref="ResourceHttpResult"/> presents a download.
/// </summary>
public sealed class ResourceResultOptions
{
    /// <summary>
    /// Let the browser display the content (<c>Content-Disposition: inline</c>) instead of offering it as a download
    /// (<c>attachment</c>, the default). Use it only for content types your application allows: content uploaded as
    /// <c>text/html</c> or <c>image/svg+xml</c> and displayed inline runs as a page of your site.
    /// </summary>
    public bool Inline { get; init; }

    /// <summary>
    /// The file name to offer. By default it's the part of the resource's name after its last <c>/</c>, so
    /// <c>2026/10/report.pdf</c> downloads as <c>report.pdf</c>.
    /// </summary>
    public string? FileName { get; init; }
}
