namespace Resourcefull.SqlServer.Models;

internal class ResourceModel
{
    public long Id { get; set; }
    public required string ContainerName { get; set; }
    public required string Name { get; set; }
    public string? VersionId { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public bool IsLatest { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    public string? ContentHash { get; set; }
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    public string? CopiedFromContainerName { get; set; }
    public string? CopiedFromName { get; set; }
    public string? CopiedFromVersionId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Until when the version can't be deleted; null for no date.</summary>
    public DateTimeOffset? RetainUntil { get; set; }

    /// <summary>Whether the version is under a legal hold, which keeps it from being deleted until it's lifted.</summary>
    public bool LegalHold { get; set; }

    /// <summary>The length an upload session said its version would have, checked as it completes; otherwise null.</summary>
    public long? DeclaredLength { get; set; }

    /// <summary>
    /// Set while an upload stores the version's content, and moved forward as it goes; null once the version is stored.
    /// A staged version is invisible to every query but the upload's own, and the expiry sweep deletes one whose upload
    /// stopped long ago.
    /// </summary>
    public DateTimeOffset? StagedAt { get; set; }

    // Navigation properties
    public virtual ResourceContainerModel? ResourceContainer { get; set; }
    public virtual ICollection<ResourceChunkModel>? Chunks { get; set; }
}
