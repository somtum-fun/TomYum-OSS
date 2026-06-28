using Microsoft.EntityFrameworkCore;
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

        builder.Property(b => b.Mode).IsRequired();
        builder.Property(b => b.StarRating).IsRequired();
        builder.Property(b => b.TotalLength).IsRequired();
        builder.Property(b => b.MaxCombo).IsRequired();
        builder.Property(b => b.LastUpdate).IsRequired();

        // SetId is queried when refreshing/serving a whole set; index it.
        builder.HasIndex(b => b.SetId);
    }
}
