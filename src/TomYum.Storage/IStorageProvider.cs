namespace TomYum.Storage;

/// <summary>
/// A single stored object's content plus the metadata needed to serve it
/// (e.g. for an HTTP response) or stream it to a client.
/// </summary>
/// <param name="Content">The object's byte stream. The caller owns disposal.</param>
/// <param name="ContentType">MIME type, e.g. <c>image/png</c> or <c>application/octet-stream</c>.</param>
/// <param name="ContentLength">Length in bytes when known, otherwise <c>null</c>.</param>
/// <param name="ETag">Provider entity tag for cache validation, when available.</param>
public sealed record StorageObject(
    Stream Content,
    string ContentType,
    long? ContentLength,
    string? ETag);

/// <summary>
/// Abstraction over an object store backing one or more <see cref="AssetType"/>s.
/// Implementations exist for Cloudflare R2, AWS S3, OVH S3, Azure Blob, and the
/// local filesystem. Operators enable/disable providers and route each asset type
/// to a provider via configuration — no caller hardcodes a provider.
/// </summary>
/// <remarks>
/// See <c>memories/architecture/decisions/0006-storage-abstraction.md</c> and
/// <c>memories/features/storage.md</c>. All members are async and cancellable.
/// </remarks>
public interface IStorageProvider
{
    /// <summary>Stable identifier for this provider instance (e.g. <c>r2</c>, <c>ovh-s3</c>, <c>local</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Reads an object by key. Returns <c>null</c> when no object exists at <paramref name="key"/>.
    /// </summary>
    Task<StorageObject?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes (creates or overwrites) an object at <paramref name="key"/>.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object at <paramref name="key"/>. No-op when it does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns whether an object exists at <paramref name="key"/> without downloading it.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a time-limited public URL for the object when the provider supports it
    /// (used to offload large downloads like beatmap sets to the edge / CDN).
    /// Returns <c>null</c> when presigned access is unsupported or disabled.
    /// </summary>
    Task<Uri?> GetPresignedUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default);
}
