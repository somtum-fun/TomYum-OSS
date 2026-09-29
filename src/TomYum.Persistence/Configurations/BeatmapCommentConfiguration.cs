using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="BeatmapComment"/>.</summary>
public sealed class BeatmapCommentConfiguration : IEntityTypeConfiguration<BeatmapComment>
{
    public void Configure(EntityTypeBuilder<BeatmapComment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("beatmap_comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedOnAdd();
        builder.Property(c => c.TargetType).IsRequired().HasMaxLength(8);
        builder.Property(c => c.Text).IsRequired().HasMaxLength(80);
        builder.Property(c => c.Colour).HasMaxLength(6);
        builder.HasIndex(c => new { c.TargetType, c.TargetId });
    }
}
