using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="CustomBeatmapSet"/>: one row per custom set, found by owner too.</summary>
public sealed class CustomBeatmapSetConfiguration : IEntityTypeConfiguration<CustomBeatmapSet>
{
    public void Configure(EntityTypeBuilder<CustomBeatmapSet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("custom_beatmapsets");
        builder.HasKey(s => s.SetId);
        builder.Property(s => s.SetId).HasColumnName("set_id").ValueGeneratedNever();
        builder.Property(s => s.OwnerId).HasColumnName("owner_id");
        builder.Property(s => s.Target).HasColumnName("target");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UploadedAt).HasColumnName("uploaded_at");
        builder.HasIndex(s => s.OwnerId);
    }
}

/// <summary>Maps <see cref="CustomBeatmapSlot"/>: difficulty ids handed out for custom sets.</summary>
public sealed class CustomBeatmapSlotConfiguration : IEntityTypeConfiguration<CustomBeatmapSlot>
{
    public void Configure(EntityTypeBuilder<CustomBeatmapSlot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("custom_beatmap_slots");
        builder.HasKey(s => s.BeatmapId);
        builder.Property(s => s.BeatmapId).HasColumnName("beatmap_id").ValueGeneratedNever();
        builder.Property(s => s.SetId).HasColumnName("set_id");
        builder.HasIndex(s => s.SetId);
    }
}
