namespace TomYum.Storage;

/// <summary>
/// Which object-store backend a provider entry uses.
/// </summary>
public enum StorageProviderKind
{
    /// <summary>Local static files / folder on the host or a mounted volume.</summary>
    Local,

    /// <summary>Cloudflare R2 (S3-compatible).</summary>
    CloudflareR2,

    /// <summary>Amazon S3.</summary>
    AwsS3,

    /// <summary>OVH Object Storage (S3-compatible).</summary>
    OvhS3,

    /// <summary>Azure Blob Storage.</summary>
    AzureBlob,
}

/// <summary>
/// Configuration for a single named provider instance. Bound from the
/// <c>storage:providers</c> section. Secret fields (keys) come from environment
/// variables / k8s secrets, never committed config. See ADR-0006 and ADR-0008.
/// </summary>
public sealed class StorageProviderOptions
{
    /// <summary>Whether this provider may be used at all. Master off-switch per provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The backend kind this entry configures.</summary>
    public StorageProviderKind Kind { get; set; }

    /// <summary>Bucket / container name (object stores) or root path (local).</summary>
    public string? Bucket { get; set; }

    /// <summary>Service endpoint URL for S3-compatible providers (R2/OVH/custom).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Region, when the provider requires one.</summary>
    public string? Region { get; set; }

    /// <summary>Public base URL used to build presigned / CDN links, when applicable.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Access key id. Inject from secrets; do not commit.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Secret access key. Inject from secrets; do not commit.</summary>
    public string? SecretKey { get; set; }
}

/// <summary>
/// Root storage configuration. Supports enable-all, disable-all, and selective
/// per-asset-type routing across the configured providers.
/// </summary>
/// <remarks>
/// Resolution order for a given <see cref="AssetType"/>:
/// <list type="number">
/// <item>If <see cref="Enabled"/> is <c>false</c>, storage is globally disabled.</item>
/// <item>If <see cref="Routes"/> contains the asset type, use that provider.</item>
/// <item>Otherwise fall back to <see cref="DefaultProvider"/>.</item>
/// </list>
/// A route pointing at a disabled provider is a configuration error surfaced at startup.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "storage";

    /// <summary>Global master switch. When <c>false</c>, all asset storage is disabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the provider used when an asset type has no explicit route.</summary>
    public string? DefaultProvider { get; set; }

    /// <summary>Named provider instances, keyed by provider name (e.g. <c>r2</c>, <c>local</c>).</summary>
    public IDictionary<string, StorageProviderOptions> Providers { get; } =
        new Dictionary<string, StorageProviderOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-asset-type provider routing. Maps an <see cref="AssetType"/> to a provider name
    /// present in <see cref="Providers"/>. Asset types absent here use <see cref="DefaultProvider"/>.
    /// </summary>
    public IDictionary<AssetType, string> Routes { get; } =
        new Dictionary<AssetType, string>();
}

/// <summary>
/// Resolves the correct <see cref="IStorageProvider"/> for a given <see cref="AssetType"/>
/// according to <see cref="StorageOptions"/>. Callers depend on this rather than on a
/// concrete provider, so routing stays a configuration concern.
/// </summary>
public interface IStorageRouter
{
    /// <summary>
    /// Returns the provider responsible for <paramref name="assetType"/>, or <c>null</c>
    /// when storage is disabled (globally or for that asset type).
    /// </summary>
    IStorageProvider? Resolve(AssetType assetType);
}
