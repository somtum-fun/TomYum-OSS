using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>
/// Fluent API mapping for <see cref="User"/>. Keeps EF concerns out of the domain entity
/// (ADR-0001/0002): the canonical <see cref="User.SafeName"/> and <see cref="User.Email"/> are
/// uniquely indexed, and <see cref="User.Privileges"/> is stored as its underlying int.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        // osu! user ids are assigned by the server; let MySQL auto-increment.
        builder.Property(u => u.Id).ValueGeneratedOnAdd();

        builder.Property(u => u.Username).HasMaxLength(32).IsRequired();

        builder.Property(u => u.SafeName).HasMaxLength(32).IsRequired();
        builder.HasIndex(u => u.SafeName).IsUnique();

        builder.Property(u => u.Email).HasMaxLength(254).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();

        // bcrypt(md5_hex(plaintext)) — always 60 chars, but allow headroom.
        builder.Property(u => u.PasswordBcrypt).HasMaxLength(60).IsRequired();

        builder.Property(u => u.Country).HasMaxLength(2).IsRequired();

        builder.Property(u => u.CreationTime).IsRequired();
        builder.Property(u => u.LatestActivity).IsRequired();

        builder.Property(u => u.ApiKey).HasMaxLength(64);
        builder.HasIndex(u => u.ApiKey).IsUnique();

        // Store the privileges flags enum as its underlying int.
        builder.Property(u => u.Privileges).HasConversion<int>().IsRequired();

        // Moderation state comes from osu-web's account data (osu-web mode only).
        builder.Ignore(u => u.IsRestricted);
        builder.Ignore(u => u.SilenceEndUtc);
        builder.Ignore(u => u.IsBot);
    }
}
