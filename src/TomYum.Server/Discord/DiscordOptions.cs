namespace TomYum.Server.Discord;

/// <summary>
/// The Discord bot (<c>Discord</c> config section, e.g. <c>TOMYUM_DISCORD__TOKEN</c>). It runs when
/// <see cref="Token"/> and <see cref="GuildId"/> are set. Channel and role ids are Discord snowflakes
/// (Developer Mode → right-click → Copy ID); a channel or role left at 0 is simply not used.
/// See docs/discord.md.
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>The bot token (Discord developer portal → Bot → Reset Token). Secret.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The server (guild) the bot serves; its slash commands are registered there.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Two-way relay with the in-game chat channel <see cref="ChatChannel"/>.</summary>
    public ulong ChatChannelId { get; set; }

    /// <summary>Ranked, qualified and loved maps.</summary>
    public ulong BeatmapsChannelId { get; set; }

    /// <summary>Public announcements: #1s, big plays, medals, new players.</summary>
    public ulong PublicLogChannelId { get; set; }

    /// <summary>Staff: server-side staff actions (imports, recalculations, score removals) and errors.</summary>
    public ulong ServerLogChannelId { get; set; }

    /// <summary>Staff: actions on accounts (restrictions, silences, …), blocked ban evasion, links.</summary>
    public ulong UserLogChannelId { get; set; }

    /// <summary>Discord roles given to linked players in these website groups.</summary>
    /// <summary>Where the self-updating status message is pinned (0: none).</summary>
    public ulong StatusChannelId { get; set; }

    /// <summary>Where reports and suspicious plays go, with staff buttons (0: the user log).</summary>
    public ulong ReportsChannelId { get; set; }

    /// <summary>Where the map of the day is posted (0: the public log).</summary>
    public ulong MapOfTheDayChannelId { get; set; }

    /// <summary>The role the map of the day's winner holds for a week (0: the bot makes a "Map of the Day" role).</summary>
    public ulong MapOfTheDayRoleId { get; set; }

    public ulong AdminRoleId { get; set; }

    public ulong GmtRoleId { get; set; }

    public ulong NatRoleId { get; set; }

    public ulong BnRoleId { get; set; }

    /// <summary>Given to every linked player (0 = none).</summary>
    public ulong LinkedRoleId { get; set; }

    /// <summary>The in-game channel relayed with <see cref="ChatChannelId"/>.</summary>
    public string ChatChannel { get; set; } = "#osu";

    /// <summary>Plays worth at least this much pp are announced.</summary>
    public double BigPlayPp { get; set; } = 300;

    /// <summary>The website, e.g. <c>https://osu.sundei.eu</c>; defaults to <c>https://osu.&lt;Server:Domain&gt;</c>.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Avatars, e.g. <c>https://a.sundei.eu</c>; defaults to <c>https://a.&lt;Server:Domain&gt;</c>.</summary>
    public string AvatarUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && GuildId != 0;

    /// <summary>
    /// Reads the section. Unlike the configuration binder, an empty id (an env file line with no
    /// value) means "not used" instead of failing; an id that isn't a number is an error that says so.
    /// </summary>
    public static DiscordOptions Read(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        ulong Id(string key)
        {
            var text = section[key];
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            return ulong.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? id
                : throw new InvalidOperationException($"Discord:{key} should be a Discord id (a number), not \"{text}\".");
        }

        string Text(string key, string fallback) => string.IsNullOrWhiteSpace(section[key]) ? fallback : section[key]!.Trim();

        var defaults = new DiscordOptions();
        return new DiscordOptions
        {
            Token = Text(nameof(Token), string.Empty),
            GuildId = Id(nameof(GuildId)),
            ChatChannelId = Id(nameof(ChatChannelId)),
            BeatmapsChannelId = Id(nameof(BeatmapsChannelId)),
            PublicLogChannelId = Id(nameof(PublicLogChannelId)),
            ServerLogChannelId = Id(nameof(ServerLogChannelId)),
            UserLogChannelId = Id(nameof(UserLogChannelId)),
            StatusChannelId = Id(nameof(StatusChannelId)),
            ReportsChannelId = Id(nameof(ReportsChannelId)),
            MapOfTheDayChannelId = Id(nameof(MapOfTheDayChannelId)),
            MapOfTheDayRoleId = Id(nameof(MapOfTheDayRoleId)),
            AdminRoleId = Id(nameof(AdminRoleId)),
            GmtRoleId = Id(nameof(GmtRoleId)),
            NatRoleId = Id(nameof(NatRoleId)),
            BnRoleId = Id(nameof(BnRoleId)),
            LinkedRoleId = Id(nameof(LinkedRoleId)),
            ChatChannel = Text(nameof(ChatChannel), defaults.ChatChannel),
            BigPlayPp = double.TryParse(section[nameof(BigPlayPp)], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pp) && pp > 0
                ? pp
                : defaults.BigPlayPp,
            SiteUrl = Text(nameof(SiteUrl), string.Empty),
            AvatarUrl = Text(nameof(AvatarUrl), string.Empty),
        };
    }

    /// <summary>The Discord roles for website group identifiers.</summary>
    /// <summary>Where reports go.</summary>
    public ulong ReportsChannel => ReportsChannelId != 0 ? ReportsChannelId : UserLogChannelId;

    /// <summary>Where the map of the day goes.</summary>
    public ulong MapOfTheDayChannel => MapOfTheDayChannelId != 0 ? MapOfTheDayChannelId : PublicLogChannelId;

    public IReadOnlyDictionary<string, ulong> GroupRoles() => new Dictionary<string, ulong>(StringComparer.Ordinal)
    {
        ["admin"] = AdminRoleId,
        ["gmt"] = GmtRoleId,
        ["nat"] = NatRoleId,
        ["bng"] = BnRoleId,
        ["bng_limited"] = BnRoleId,
    };

    /// <summary>Every role the bot manages (it never touches others).</summary>
    public IReadOnlySet<ulong> ManagedRoles() =>
        GroupRoles().Values.Append(LinkedRoleId).Where(id => id != 0).ToHashSet();
}
