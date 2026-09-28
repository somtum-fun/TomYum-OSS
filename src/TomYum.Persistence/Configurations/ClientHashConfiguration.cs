using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="ClientHash"/>: one row per account and hash combination.</summary>
public sealed class ClientHashConfiguration : IEntityTypeConfiguration<ClientHash>
{
    public void Configure(EntityTypeBuilder<ClientHash> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("client_hashes");
        builder.HasKey(h => new { h.UserId, h.OsuPath, h.Adapters, h.UninstallId, h.DiskSerial });
        builder.Property(h => h.UserId).HasColumnName("user_id");
        builder.Property(h => h.OsuPath).HasColumnName("osupath").HasMaxLength(32).IsUnicode(false);
        builder.Property(h => h.Adapters).HasColumnName("adapters").HasMaxLength(32).IsUnicode(false);
        builder.Property(h => h.UninstallId).HasColumnName("uninstall_id").HasMaxLength(32).IsUnicode(false);
        builder.Property(h => h.DiskSerial).HasColumnName("disk_serial").HasMaxLength(32).IsUnicode(false);
        builder.Property(h => h.LatestTime).HasColumnName("latest_time");
        builder.Property(h => h.Occurrences).HasColumnName("occurrences");
        builder.HasIndex(h => h.Adapters);
        builder.HasIndex(h => h.UninstallId);
        builder.HasIndex(h => h.DiskSerial);
    }
}
