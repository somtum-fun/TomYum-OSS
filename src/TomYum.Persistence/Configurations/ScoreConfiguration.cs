using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

public sealed class ScoreConfiguration : IEntityTypeConfiguration<Score>
{
    public void Configure(EntityTypeBuilder<Score> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("scores");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedOnAdd();
        builder.Property(s => s.BeatmapMd5).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Grade).HasMaxLength(2).IsRequired();
        builder.Property(s => s.Status).HasDefaultValue(ScoreStatus.Failed);
        builder.HasIndex(s => new { s.UserId, s.BeatmapMd5, s.Mode, s.Status });
        builder.HasIndex(s => new { s.BeatmapMd5, s.Mode, s.Status, s.ScoreValue });
    }
}
