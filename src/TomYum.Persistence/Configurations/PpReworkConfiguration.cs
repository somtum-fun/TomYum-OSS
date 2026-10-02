using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="PpRework"/> (pp reworks) and <see cref="PpReworkScore"/> (their new pp, kept aside).</summary>
public sealed class PpReworkConfiguration : IEntityTypeConfiguration<PpRework>, IEntityTypeConfiguration<PpReworkScore>
{
    public void Configure(EntityTypeBuilder<PpRework> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pp_reworks");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Library).HasMaxLength(16);
        builder.Property(r => r.FromVersion).HasMaxLength(100);
        builder.Property(r => r.ToVersion).HasMaxLength(100);
        builder.Property(r => r.Modes).HasMaxLength(32);
        builder.Property(r => r.State).HasMaxLength(16);
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.Error).HasMaxLength(1000);
        builder.Ignore(r => r.ModeList);
        builder.Ignore(r => r.IsActive);
        builder.HasIndex(r => r.State);
    }

    public void Configure(EntityTypeBuilder<PpReworkScore> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pp_rework_scores");
        builder.HasKey(s => new { s.ReworkId, s.ScoreId });
    }
}
