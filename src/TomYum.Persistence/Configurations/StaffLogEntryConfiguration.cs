using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="StaffLogEntry"/>; must match <c>SchemaGuard</c>'s <c>staff_log</c> DDL.</summary>
public sealed class StaffLogEntryConfiguration : IEntityTypeConfiguration<StaffLogEntry>
{
    public void Configure(EntityTypeBuilder<StaffLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("staff_log");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.Action).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Summary).IsRequired().HasMaxLength(512);
        builder.Property(e => e.Result).HasMaxLength(32);
        builder.Property(e => e.JobId).HasMaxLength(32);
        builder.Property(e => e.Via).HasMaxLength(16);
        builder.HasIndex(e => new { e.Action, e.Id });
        builder.HasIndex(e => e.TargetId);
        builder.HasIndex(e => e.JobId);
    }
}
