using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="LastFmLink"/>: one Last.fm account per player.</summary>
public sealed class LastFmLinkConfiguration : IEntityTypeConfiguration<LastFmLink>
{
    public void Configure(EntityTypeBuilder<LastFmLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("lastfm_links");
        builder.HasKey(l => l.UserId);
        builder.Property(l => l.UserId).ValueGeneratedNever();
        builder.Property(l => l.Username).IsRequired().HasMaxLength(64);
        builder.Property(l => l.SessionKey).IsRequired().HasMaxLength(64);
    }
}

/// <summary>Maps <see cref="LastFmScrobble"/>: the recent scrobbles, newest last.</summary>
public sealed class LastFmScrobbleConfiguration : IEntityTypeConfiguration<LastFmScrobble>
{
    public void Configure(EntityTypeBuilder<LastFmScrobble> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("lastfm_scrobbles");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedOnAdd();
        builder.Property(s => s.Artist).IsRequired().HasMaxLength(255);
        builder.Property(s => s.Title).IsRequired().HasMaxLength(255);
        builder.Property(s => s.Client).IsRequired().HasMaxLength(8);
        builder.HasIndex(s => new { s.UserId, s.Id });
    }
}
