using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="PpSnapshot"/>: one row per player, mode and day.</summary>
public sealed class PpSnapshotConfiguration : IEntityTypeConfiguration<PpSnapshot>
{
    public void Configure(EntityTypeBuilder<PpSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pp_history");
        builder.HasKey(s => new { s.UserId, s.Mode, s.Day });
        builder.HasIndex(s => s.Day);
    }
}
