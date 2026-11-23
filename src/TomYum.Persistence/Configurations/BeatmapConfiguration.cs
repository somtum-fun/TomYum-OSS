using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>
/// Fluent-API mapping for <see cref="Beatmap"/>. Kept here (not as data annotations on the
/// entity) so the domain stays free of EF concerns — see ADR-0001/0002. Mirrors bancho.py's
/// <c>maps</c> table shape: keyed by beatmap id, with the MD5 hash as the wire-identity lookup.
/// </summary>
public sealed class BeatmapConfiguration : IEntityTypeConfiguration<Beatmap>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Beatmap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("beatmaps");

        // Beatmap id is the natural key. It is NOT database-generated: ids come from the osu!
        // API (official maps) or the reserved custom range (>= Beatmap.CustomIdFloor, ADR-0009).
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.SetId).IsRequired();

        // MD5 of the .osu file — the wire identity used by both protocols. 32 hex chars; indexed
        // because getscores and login resolve a map by hash, not id.
        builder.Property(b => b.Md5).HasMaxLength(32).IsRequired();
        builder.HasIndex(b => b.Md5).IsUnique();

        builder.Property(b => b.Artist).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Title).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Version).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Creator).HasMaxLength(64).IsRequired();

        // Stored as the internal RankedStatus integer (wire-significant; see the enum).
        builder.Property(b => b.Status).HasConversion<int>().IsRequired();
        builder.Property(b => b.StatusFrozen).IsRequired();
        // What osu!'s API said about the map: its own status number and the details (object counts, CS/AR/OD/HP,
        // dates, ...). Kept so a stored ranked map answers a lookup as fully as osu! does (BeatmapService), and
        // filled in whenever the map is stored from a lookup or an import; null for maps stored before.
        builder.Property(b => b.OsuApiStatus);
        builder.Property(b => b.Details)
            .HasColumnType("json")
            .HasConversion(
                details => details == null ? null : JsonSerializer.Serialize(details, (JsonSerializerOptions?)null),
                json => json == null ? null : JsonSerializer.Deserialize<TomYum.Domain.Entities.BeatmapDetails>(json, (JsonSerializerOptions?)null),
                new ValueComparer<TomYum.Domain.Entities.BeatmapDetails?>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    d => d == null ? 0 : JsonSerializer.Serialize(d, (JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
                    d => d == null ? null : JsonSerializer.Deserialize<TomYum.Domain.Entities.BeatmapDetails>(JsonSerializer.Serialize(d, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)));

        builder.Property(b => b.Mode).IsRequired();
        builder.Property(b => b.StarRating).IsRequired();
        builder.Property(b => b.TotalLength).IsRequired();
        builder.Property(b => b.MaxCombo).IsRequired();
        builder.Property(b => b.LastUpdate).IsRequired();

        // SetId is queried when refreshing/serving a whole set; index it.
        builder.HasIndex(b => b.SetId);
    }
}
