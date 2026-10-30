namespace Resourcefull.AspNetCore;

/// <summary>
/// How <see cref="ResourceHttpResult"/> presents a download.
/// </summary>
public sealed class ResourceResultOptions
{
    /// <summary>
    /// Let the browser display the content (<c>Content-Disposition: inline</c>) instead of offering it as a download
    /// (<c>attachment</c>, the default). Only <see cref="InlineContentTypes"/> are displayed; anything else is still
    /// offered as a download, because content uploaded as <c>text/html</c> or <c>image/svg+xml</c> and displayed
    /// inline would run as a page of your site.
    /// </summary>
    public bool Inline { get; init; }

    /// <summary>
    /// The content types <see cref="Inline"/> displays, by default <see cref="DefaultInlineContentTypes"/>. A pattern
    /// can end in <c>/*</c> to cover every subtype. Add a type only if no one can upload content of that type that
    /// should not run as a page of your site.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public IReadOnlyCollection<string> InlineContentTypes { get; init; } = DefaultInlineContentTypes;

    /// <summary>
    /// Content types that are safe to display: common image formats (PNG, JPEG, GIF, WebP, AVIF and BMP; not SVG),
    /// PDF, audio, video and plain text.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public static IReadOnlyList<string> DefaultInlineContentTypes { get; } =
        ["image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "application/pdf", "audio/*", "video/*", "text/plain"];

    /// <summary>
    /// The <c>Cache-Control</c> header sent with the content, and with 304 Not Modified. By default
    /// <c>private, no-cache</c>: only the user's own browser keeps a copy, and it checks with your application before
    /// reusing it, which the <c>ETag</c> makes cheap. Shared caches, such as CDNs and proxies, don't keep it, so one
    /// user's file is never served to another. For public files at URLs whose content never changes, such as URLs that
    /// name a version, something like <c>public, max-age=31536000, immutable</c> lets every cache keep them.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public string CacheControl { get; init; } = "private, no-cache";

    /// <summary>
    /// The file name to offer. By default it's the part of the resource's name after its last <c>/</c>, so
    /// <c>2026/10/report.pdf</c> downloads as <c>report.pdf</c>.
    /// </summary>
    public string? FileName { get; init; }
}
