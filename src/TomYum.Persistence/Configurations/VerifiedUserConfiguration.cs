using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="VerifiedUser"/>.</summary>
public sealed class VerifiedUserConfiguration : IEntityTypeConfiguration<VerifiedUser>
{
    public void Configure(EntityTypeBuilder<VerifiedUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("verified_users");
        builder.HasKey(v => v.UserId);
        builder.Property(v => v.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(v => v.VerifiedAt).HasColumnName("verified_at");
        builder.Property(v => v.VerifiedBy).HasColumnName("verified_by");
    }
}
