using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="DiscordLink"/>: one row per linked account, unique both ways.</summary>
public sealed class DiscordLinkConfiguration : IEntityTypeConfiguration<DiscordLink>
{
    public void Configure(EntityTypeBuilder<DiscordLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("discord_links");
        builder.HasKey(l => l.UserId);
        builder.Property(l => l.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(l => l.DiscordId).HasColumnName("discord_id");
        builder.Property(l => l.LinkedAt).HasColumnName("linked_at");
        builder.HasIndex(l => l.DiscordId).IsUnique();
    }
}
