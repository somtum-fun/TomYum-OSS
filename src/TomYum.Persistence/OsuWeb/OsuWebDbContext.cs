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

    public DbSet<OsuWebBeatmapset> Beatmapsets => Set<OsuWebBeatmapset>();

    public DbSet<OsuWebBeatmap> Beatmaps => Set<OsuWebBeatmap>();

    public DbSet<OsuWebScore> Scores => Set<OsuWebScore>();

    public DbSet<OsuWebAccountHistory> AccountHistory => Set<OsuWebAccountHistory>();

    public DbSet<OsuWebBeatmapPack> BeatmapPacks => Set<OsuWebBeatmapPack>();

    public DbSet<OsuWebBeatmapPackItem> BeatmapPackItems => Set<OsuWebBeatmapPackItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OsuWebBeatmapPack>(pack =>
        {
            pack.ToTable("osu_beatmappacks");
            pack.HasKey(p => p.PackId);
            pack.Property(p => p.PackId).HasColumnName("pack_id").ValueGeneratedOnAdd();
            pack.Property(p => p.Url).HasColumnName("url");
            pack.Property(p => p.Name).HasColumnName("name");
            pack.Property(p => p.Author).HasColumnName("author");
            pack.Property(p => p.Tag).HasColumnName("tag");
            pack.Property(p => p.Date).HasColumnName("date");
            pack.Property(p => p.Hidden).HasColumnName("hidden");
        });

        modelBuilder.Entity<OsuWebBeatmapPackItem>(item =>
        {
            item.ToTable("osu_beatmappacks_items");
            item.HasKey(i => i.ItemId);
            item.Property(i => i.ItemId).HasColumnName("item_id").ValueGeneratedOnAdd();
            item.Property(i => i.PackId).HasColumnName("pack_id");
            item.Property(i => i.BeatmapsetId).HasColumnName("beatmapset_id");
        });

        modelBuilder.Entity<OsuWebAccountHistory>(history =>
        {
            history.ToTable("osu_user_banhistory");
            history.HasKey(h => h.BanId);
            history.Property(h => h.BanId).HasColumnName("ban_id");
            history.Property(h => h.UserId).HasColumnName("user_id");
            history.Property(h => h.BanStatus).HasColumnName("ban_status");
            history.Property(h => h.Period).HasColumnName("period");
            history.Property(h => h.Timestamp).HasColumnName("timestamp");
            history.Property(h => h.Reason).HasColumnName("reason");
            history.Property(h => h.BannerId).HasColumnName("banner_id");
            history.Property(h => h.Permanent).HasColumnName("permanent");
        });

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

        modelBuilder.Entity<OsuWebBeatmapset>(set =>
        {
            set.ToTable("osu_beatmapsets");
            set.HasKey(s => s.BeatmapsetId);
            // Ids come from osu! (or TomYum's custom range), never from the auto-increment.
            set.Property(s => s.BeatmapsetId).HasColumnName("beatmapset_id").ValueGeneratedNever();
            set.Property(s => s.Artist).HasColumnName("artist");
            set.Property(s => s.Title).HasColumnName("title");
            set.Property(s => s.Creator).HasColumnName("creator");
            set.Property(s => s.Approved).HasColumnName("approved");
            set.Property(s => s.ApprovedDate).HasColumnName("approved_date");
            set.Property(s => s.SubmitDate).HasColumnName("submit_date");
            set.Property(s => s.LastUpdate).HasColumnName("last_update");
            set.Property(s => s.CoverUpdatedAt).HasColumnName("cover_updated_at");
            set.Property(s => s.Bpm).HasColumnName("bpm");
            set.Property(s => s.GenreId).HasColumnName("genre_id");
            set.Property(s => s.LanguageId).HasColumnName("language_id");
            set.Property(s => s.Source).HasColumnName("source");
            set.Property(s => s.Tags).HasColumnName("tags");
            set.Property(s => s.Video).HasColumnName("video");
            set.Property(s => s.Storyboard).HasColumnName("storyboard");
        });

        modelBuilder.Entity<OsuWebBeatmap>(map =>
        {
            map.ToTable("osu_beatmaps");
            map.HasKey(b => b.BeatmapId);
            map.Property(b => b.BeatmapId).HasColumnName("beatmap_id").ValueGeneratedNever();
            map.Property(b => b.BeatmapsetId).HasColumnName("beatmapset_id");
            map.Property(b => b.Checksum).HasColumnName("checksum");
            map.Property(b => b.Filename).HasColumnName("filename");
            map.Property(b => b.Version).HasColumnName("version");
            map.Property(b => b.TotalLength).HasColumnName("total_length");
            map.Property(b => b.HitLength).HasColumnName("hit_length");
            map.Property(b => b.Playmode).HasColumnName("playmode");
            map.Property(b => b.Approved).HasColumnName("approved");
            map.Property(b => b.LastUpdate).HasColumnName("last_update");
            map.Property(b => b.Difficultyrating).HasColumnName("difficultyrating");
            map.Property(b => b.MaxCombo).HasColumnName("max_combo");
            map.Property(b => b.DiffSize).HasColumnName("diff_size");
            map.Property(b => b.DiffApproach).HasColumnName("diff_approach");
            map.Property(b => b.DiffOverall).HasColumnName("diff_overall");
            map.Property(b => b.DiffDrain).HasColumnName("diff_drain");
            map.Property(b => b.Bpm).HasColumnName("bpm");
            map.Property(b => b.CountNormal).HasColumnName("countNormal");
            map.Property(b => b.CountSlider).HasColumnName("countSlider");
            map.Property(b => b.CountSpinner).HasColumnName("countSpinner");
            map.Property(b => b.CountTotal).HasColumnName("countTotal");
        });

        modelBuilder.Entity<OsuWebScore>(score =>
        {
            score.ToTable("scores");
            // The table's primary key is (id, preserve, unix_updated_at); id alone is unique and
            // auto-increments. unix_updated_at is left to its database default.
            score.HasKey(s => s.Id);
            score.Property(s => s.Id).HasColumnName("id").ValueGeneratedOnAdd();
            score.Property(s => s.UserId).HasColumnName("user_id");
            score.Property(s => s.RulesetId).HasColumnName("ruleset_id");
            score.Property(s => s.BeatmapId).HasColumnName("beatmap_id");
            score.Property(s => s.HasReplay).HasColumnName("has_replay");
            score.Property(s => s.Preserve).HasColumnName("preserve");
            score.Property(s => s.Ranked).HasColumnName("ranked");
            score.Property(s => s.Rank).HasColumnName("rank");
            score.Property(s => s.Passed).HasColumnName("passed");
            score.Property(s => s.Accuracy).HasColumnName("accuracy");
            score.Property(s => s.MaxCombo).HasColumnName("max_combo");
            score.Property(s => s.TotalScore).HasColumnName("total_score");
            score.Property(s => s.Data).HasColumnName("data").HasColumnType("json");
            score.Property(s => s.Pp).HasColumnName("pp");
            score.Property(s => s.LegacyScoreId).HasColumnName("legacy_score_id");
            score.Property(s => s.LegacyTotalScore).HasColumnName("legacy_total_score");
            score.Property(s => s.EndedAt).HasColumnName("ended_at");
        });

        base.OnModelCreating(modelBuilder);
    }
}
