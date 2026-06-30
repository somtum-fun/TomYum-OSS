using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

public sealed class UserStatsConfiguration : IEntityTypeConfiguration<UserStats>
{
    public void Configure(EntityTypeBuilder<UserStats> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_stats");
        builder.HasKey(s => new { s.UserId, s.Mode });
        builder.Property(s => s.Accuracy).IsRequired();
        builder.Property(s => s.Pp).IsRequired();
    }
}
