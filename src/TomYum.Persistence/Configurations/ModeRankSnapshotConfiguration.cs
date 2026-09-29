using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="ModeRankSnapshot"/>: one row per player, mode and day.</summary>
public sealed class ModeRankSnapshotConfiguration : IEntityTypeConfiguration<ModeRankSnapshot>
{
    public void Configure(EntityTypeBuilder<ModeRankSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("mode_rank_history");
        builder.HasKey(s => new { s.UserId, s.Mode, s.Day });
        builder.HasIndex(s => s.Day);
    }
}
