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
/// Routing for a single <see cref="AssetType"/>. Each asset type may be served from
/// one provider and replicated to any combination of providers on write — so an operator
/// can host an asset on R2 only, on OVH only, on local only, on several at once, or on all.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WriteTo"/> lists every provider an upload is fanned out to (the "host it on
/// all/some of them" requirement). <see cref="ReadFrom"/> names the single provider used to
/// serve reads / presign URLs; when omitted it defaults to the first entry of
/// <see cref="WriteTo"/>. <see cref="ReadFrom"/> should appear in <see cref="WriteTo"/> so
/// reads hit a provider that actually holds the object.
/// </para>
/// </remarks>
public sealed class AssetRoute
{
    /// <summary>Per-asset-type switch. When <c>false</c>, this asset type is not stored at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Providers this asset type is written to (replicated across) on upload.</summary>
    public IList<string> WriteTo { get; } = new List<string>();

    /// <summary>Provider used to serve reads. Defaults to the first <see cref="WriteTo"/> entry when null.</summary>
    public string? ReadFrom { get; set; }
}

/// <summary>
/// Root storage configuration. Supports enable-all, disable-all, and selective
/// per-asset-type routing where each asset type targets one, several, or all providers.
/// </summary>
/// <remarks>
/// Resolution for a given <see cref="AssetType"/>:
/// <list type="number">
/// <item>If <see cref="Enabled"/> is <c>false</c>, storage is globally disabled.</item>
/// <item>If <see cref="Routes"/> has an entry and it is enabled, use its <c>WriteTo</c>/<c>ReadFrom</c> set.</item>
/// <item>Otherwise fall back to <see cref="DefaultProviders"/>.</item>
/// </list>
/// A route naming a disabled or unknown provider is a configuration error surfaced at startup.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "storage";

    /// <summary>Global master switch. When <c>false</c>, all asset storage is disabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Providers used when an asset type has no explicit route. The first entry serves reads;
    /// all entries receive writes. Lets an operator default every asset type to "all providers".
    /// </summary>
    public IList<string> DefaultProviders { get; } = new List<string>();

    /// <summary>Named provider instances, keyed by provider name (e.g. <c>r2</c>, <c>local</c>).</summary>
    public IDictionary<string, StorageProviderOptions> Providers { get; } =
        new Dictionary<string, StorageProviderOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-asset-type routing. Asset types absent here fall back to <see cref="DefaultProviders"/>.
    /// </summary>
    public IDictionary<AssetType, AssetRoute> Routes { get; } =
        new Dictionary<AssetType, AssetRoute>();
}

/// <summary>
/// Resolves the <see cref="IStorageProvider"/>(s) for a given <see cref="AssetType"/>
/// according to <see cref="StorageOptions"/>. Callers depend on this rather than on a
/// concrete provider, so routing — including multi-target replication — stays a config concern.
/// </summary>
public interface IStorageRouter
{
    /// <summary>
    /// Returns the provider that serves reads for <paramref name="assetType"/>, or <c>null</c>
    /// when storage is disabled (globally or for that asset type).
    /// </summary>
    IStorageProvider? ResolveRead(AssetType assetType);

    /// <summary>
    /// Returns every provider an upload of <paramref name="assetType"/> is written to.
    /// Empty when storage is disabled for that asset type. A write succeeds only when all
    /// targeted providers succeed (callers may relax this to best-effort per policy).
    /// </summary>
    IReadOnlyList<IStorageProvider> ResolveWrite(AssetType assetType);
}
