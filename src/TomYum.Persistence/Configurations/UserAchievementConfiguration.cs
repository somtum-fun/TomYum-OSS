using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

public sealed class UserAchievementConfiguration : IEntityTypeConfiguration<UserAchievement>
{
    public void Configure(EntityTypeBuilder<UserAchievement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_achievements");
        builder.HasKey(a => new { a.UserId, a.AchievementId });
        builder.Property(a => a.UnlockedAt).IsRequired();
    }
}
