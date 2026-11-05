namespace Resourcefull.AspNetCore;

/// <summary>
/// What a signed upload link lets its holder upload, and for how long. Set either <see cref="ResourceName"/>, for a
/// link that uploads one file, or <see cref="NamePrefix"/>, for one that uploads any number of files under names the
/// holder picks. Pass it to <see cref="IResourceLinkSigner.CreateUploadToken"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container uploads go into.</param>
public sealed record ResourceUploadLink(string ContainerName)
{
    /// <summary>The one name the link uploads to. It can't be combined with <see cref="NamePrefix"/>.</summary>
    public string? ResourceName { get; init; }

    /// <summary>
    /// What every name the link uploads to starts with: the holder picks the rest, so with <c>claims/1042/</c>, an upload
    /// of <c>photo.jpg</c> is stored as <c>claims/1042/photo.jpg</c>. End it with a separator such as <c>/</c>, or
    /// <c>claims/1042</c> would also let the holder upload <c>claims/10421</c>. It can't be combined with
    /// <see cref="ResourceName"/>.
    /// </summary>
    public string? NamePrefix { get; init; }

    /// <summary>How long the link works, from when it's created: one hour unless you say otherwise.</summary>
    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The most bytes one upload may hold, or <see langword="null"/> for no limit of the link's own. With a limit, a
    /// body sent to <c>Results.SharedUpload</c> must have a <c>Content-Length</c>, and is refused before it's read if
    /// that's over the limit; each file a form posts is refused once it goes past the limit; and a resumable upload is
    /// refused when it starts if the length it declares is over it.
    /// </summary>
    public long? MaxSize { get; init; }

    /// <summary>
    /// The content types an upload may declare, such as <c>application/pdf</c>, or <c>image/*</c> for every image type,
    /// compared as <see cref="ResourceContainerPolicy.AllowedContentTypes"/> compares them; an upload that declares no
    /// type is refused. <see langword="null"/> allows any type, or none.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedContentTypes { get; init; }

    /// <summary>
    /// What an upload does when its name already has a current version: <see cref="ResourceWriteMode.Create"/>, the
    /// default, refuses it, so the link can't overwrite anything; <see cref="ResourceWriteMode.Replace"/> stores the
    /// upload as the name's new version.
    /// </summary>
    public ResourceWriteMode Mode { get; init; } = ResourceWriteMode.Create;
}

/// <summary>
/// What a valid upload token lets its holder upload.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container uploads go into.</param>
/// <param name="ResourceName">The one name the link uploads to, or <see langword="null"/> for a link with a prefix.</param>
/// <param name="NamePrefix">What every name the link uploads to starts with, or <see langword="null"/> for a link to one name.</param>
/// <param name="MaxSize">The most bytes one upload may hold, or <see langword="null"/> for no limit of the link's own.</param>
/// <param name="AllowedContentTypes">The content types an upload may declare, or <see langword="null"/> for any.</param>
/// <param name="Mode">What an upload does when its name already has a current version.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record SignedResourceUploadLink(string ContainerName, string? ResourceName, string? NamePrefix, long? MaxSize,
    IReadOnlyCollection<string>? AllowedContentTypes, ResourceWriteMode Mode, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// The name an upload through the link is stored as: the link's own name when <paramref name="name"/> is empty, or
    /// the prefix followed by <paramref name="name"/>; <see langword="null"/> when the link doesn't cover it, because a
    /// link to one name was given a name, or a link with a prefix wasn't.
    /// </summary>
    /// <param name="name">The name the holder gave, such as from the URL, or <see langword="null"/> for none.</param>
    public string? NameFor(string? name) => string.IsNullOrEmpty(name)
        ? ResourceName
        : NamePrefix is null ? null : NamePrefix + name;
}

/// <summary>
/// The result of checking an upload token.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Status">Whether it's a link that still works.</param>
/// <param name="Link">What it allows, for a valid or expired token; <see langword="null"/> for an invalid one.</param>
public sealed record ResourceUploadLinkCheck(ResourceLinkStatus Status, SignedResourceUploadLink? Link);
