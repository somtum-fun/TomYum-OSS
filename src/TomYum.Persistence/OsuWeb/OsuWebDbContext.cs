using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// EF Core context over osu-web's database (<see cref="DatabaseOptions.OsuWebName"/>). osu-web's
/// migrations own this schema: never call <c>EnsureCreated</c>/<c>Migrate</c> on this context.
/// </summary>
public sealed class OsuWebDbContext : DbContext
{
    public OsuWebDbContext(DbContextOptions<OsuWebDbContext> options) : base(options)
    {
    }

    public DbSet<OsuWebUser> Users => Set<OsuWebUser>();

    public DbSet<OsuWebGroup> Groups => Set<OsuWebGroup>();

    public DbSet<OsuWebUserGroup> UserGroups => Set<OsuWebUserGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OsuWebUser>(user =>
        {
            user.ToTable("phpbb_users");
            user.HasKey(u => u.UserId);
            // Auto-increment, but an explicit id (BanchoBot = 1, the dev seed) is inserted as given.
            user.Property(u => u.UserId).HasColumnName("user_id").ValueGeneratedOnAdd();
            user.Property(u => u.UserType).HasColumnName("user_type");
            user.Property(u => u.GroupId).HasColumnName("group_id");
            user.Property(u => u.UserPermissions).HasColumnName("user_permissions");
            user.Property(u => u.UserIp).HasColumnName("user_ip");
            user.Property(u => u.UserRegdate).HasColumnName("user_regdate");
            user.Property(u => u.Username).HasColumnName("username");
            user.Property(u => u.UsernameClean).HasColumnName("username_clean");
            user.Property(u => u.UserPassword).HasColumnName("user_password");
            user.Property(u => u.UserPasschg).HasColumnName("user_passchg");
            user.Property(u => u.UserEmail).HasColumnName("user_email");
            user.Property(u => u.UserLastvisit).HasColumnName("user_lastvisit");
            user.Property(u => u.UserWarnings).HasColumnName("user_warnings");
            user.Property(u => u.UserSig).HasColumnName("user_sig");
            user.Property(u => u.UserOcc).HasColumnName("user_occ");
            user.Property(u => u.UserInterests).HasColumnName("user_interests");
            user.Property(u => u.UserColour).HasColumnName("user_colour");
            user.Property(u => u.UserRank).HasColumnName("user_rank");
            user.Property(u => u.OsuSubscriber).HasColumnName("osu_subscriber");
            user.Property(u => u.CountryAcronym).HasColumnName("country_acronym");
        });

        modelBuilder.Entity<OsuWebGroup>(group =>
        {
            group.ToTable("phpbb_groups");
            group.HasKey(g => g.GroupId);
            group.Property(g => g.GroupId).HasColumnName("group_id");
            group.Property(g => g.Identifier).HasColumnName("identifier");
            group.Property(g => g.GroupColour).HasColumnName("group_colour");
            group.Property(g => g.GroupRank).HasColumnName("group_rank");
        });

        modelBuilder.Entity<OsuWebUserGroup>(membership =>
        {
            membership.ToTable("phpbb_user_group");
            membership.HasKey(m => new { m.GroupId, m.UserId });
            membership.Property(m => m.GroupId).HasColumnName("group_id");
            membership.Property(m => m.UserId).HasColumnName("user_id");
            membership.Property(m => m.GroupLeader).HasColumnName("group_leader");
            membership.Property(m => m.UserPending).HasColumnName("user_pending");
        });

        base.OnModelCreating(modelBuilder);
    }
}
