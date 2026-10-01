using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho.Packets.Match;
using TomYum.Server.Beatmaps;
using TomYum.Server.Plugins;

namespace TomYum.Server.Bancho;

/// <summary>
/// The chat-command surface for <see cref="BanchoBot"/>: parses <c>!</c>-prefixed messages and
/// produces the bot's reply. Kept separate from <see cref="BanchoBot"/> (which owns the bot's
/// identity/presence). A command is a method with a <see cref="ChatCommandAttribute"/> (name, usage,
/// help note, who may run it): it's found by reflection and listed in <c>!help</c> automatically.
/// Plugins' commands (<see cref="PluginCommandRegistry"/>) come after the built-in ones.
/// </summary>
public sealed class BanchoBotCommands
{
    private static readonly ChatCommandTable<BanchoBotCommands> Table = new();

    private readonly TomYumVersion _version;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly MatchRegistry _matches;
    private readonly BeatmapService _beatmaps;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PluginCommandRegistry _pluginCommands;
    private readonly LoadedPluginRegistry _loadedPlugins;
    private readonly ModerationCommands? _moderation;
    private readonly Discord.DiscordLinkCodes? _discordCodes;
    private readonly MapCommands? _maps;

    public BanchoBotCommands(
        TomYumVersion version,
        PlayerRegistry players,
        ChannelRegistry channels,
        MatchRegistry matches,
        BeatmapService beatmaps,
        IServiceScopeFactory scopeFactory,
        PluginCommandRegistry pluginCommands,
        LoadedPluginRegistry loadedPlugins,
        ModerationCommands? moderation = null,
        Discord.DiscordLinkCodes? discordCodes = null,
        MapCommands? maps = null)
    {
        _version = version;
        _players = players;
        _channels = channels;
        _matches = matches;
        _beatmaps = beatmaps;
        _scopeFactory = scopeFactory;
        _pluginCommands = pluginCommands;
        _loadedPlugins = loadedPlugins;
        _moderation = moderation;
        _discordCodes = discordCodes;
        _maps = maps;
    }

    public async Task<string?> TryHandleAsync(
        OnlinePlayer caller,
        string message,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '!')
            return null;

        var parts = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var command = parts[0].ToLowerInvariant();
        var context = new ChatCommandContext(caller, command, parts, message, privileges, cancellationToken);
        if (Table.All.FirstOrDefault(c => c.Name == command) is { Access: CommandAccess.Staff } && !IsStaff(privileges))
            return StaffOnly;
        var (found, reply) = await Table.RunAsync(this, context).ConfigureAwait(false);
        return found ? reply : await FallbackToPluginAsync(caller, command, parts, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The built-in commands' names (plugins can't take these).</summary>
    public static IReadOnlyList<string> BuiltIn => Table.All.Select(c => c.Name).ToList();

    // ---- simple commands ----

    [ChatCommand("help")]
    private string HelpCommand(ChatCommandContext context) =>
        Table.Help(IsStaff(context.Privileges), _pluginCommands.Commands.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => "!" + c.Name + (c.Description.Length > 0 ? $" ({c.Description})" : string.Empty)));

    [ChatCommand("ping", Hidden = true)]
    private static string Ping(ChatCommandContext context) => "pong!";

    [ChatCommand("roll", Usage = "[max]")]
    private static string RollCommand(ChatCommandContext context) => Roll(context.Caller.Presence.Username, context.Parts);

    [ChatCommand("version")]
    private string Version(ChatCommandContext context) =>
        string.Create(CultureInfo.InvariantCulture, $"TomYum {_version.Display} (@{_version.Commit})");

    [ChatCommand("plugins", Hidden = true)]
    private string PluginsCommand(ChatCommandContext context) => ListPlugins();

    [ChatCommand("mp", Usage = "<start|abort|countdown|host|kick>")]
    private Task<string?> Mp(ChatCommandContext context) =>
        MpAsync(context.Caller, context.Parts, context.Privileges, context.CancellationToken);

    [ChatCommand("map", Usage = "<rank|love|qualify|unrank|reset> [set|map]", Help = "BN, after /np")]
    private async Task<string?> MapCommand(ChatCommandContext context) => _maps is null
        ? "Status changes aren't available on this server."
        : await _maps.RunAsync(context.Caller, context.Args, context.CancellationToken).ConfigureAwait(false);

    [ChatCommand("stats", Access = CommandAccess.Staff)]
    private string StatsCommand(ChatCommandContext context) => Stats();

    [ChatCommand("setpriv", Usage = "<user> <role>", Access = CommandAccess.Staff)]
    private Task<string> SetPriv(ChatCommandContext context) => SetPrivAsync(context.Parts, context.CancellationToken);

    // ---- moderation (ModerationCommands checks the caller: moderators count as staff there) ----

    [ChatCommand("restrict", Usage = "<user> <reason>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("ban", Usage = "<user> <reason>", Access = CommandAccess.StaffCheckedByCommand, Hidden = true)]
    [ChatCommand("unrestrict", Usage = "<user> <reason>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("unban", Usage = "<user> <reason>", Access = CommandAccess.StaffCheckedByCommand, Hidden = true)]
    [ChatCommand("silence", Usage = "<user> <10m|1h|1d|1w> <reason>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("unsilence", Usage = "<user> [reason]", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("alert", Usage = "<message>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("alertu", Usage = "<user> <message>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("alts", Usage = "<user>", Access = CommandAccess.StaffCheckedByCommand)]
    [ChatCommand("verify", Usage = "<user>", Access = CommandAccess.StaffCheckedByCommand)]
    private async Task<string?> Moderate(ChatCommandContext context) => _moderation is null
        ? "Moderation isn't available on this server."
        : await _moderation.RunAsync(context.Caller.UserId, context.Name, context.Args, context.CancellationToken).ConfigureAwait(false);

    /// <summary><c>!link &lt;code&gt;</c>: links the caller's account to the Discord account that asked for the code.</summary>
    [ChatCommand("link", Usage = "<code>", Help = "Discord")]
    private Task<string> Link(ChatCommandContext context) => LinkDiscordAsync(context.Caller, context.Parts, context.CancellationToken);

    /// <summary>
    /// <c>!irc</c>: a new IRC password (the old one stops working), always sent as a private message,
    /// even when asked in a channel.
    /// </summary>
    [ChatCommand("irc", Help = "your IRC password")]
    private async Task<string?> Irc(ChatCommandContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<Irc.IrcTokens>() is not { } tokens || scope.ServiceProvider.GetService<Irc.IrcOptions>() is not { Enabled: true } options)
            return "There's no IRC on this server.";
        if (tokens.FromWebsite)
            return string.Create(CultureInfo.InvariantCulture,
                $"Your IRC key is in the website's account settings{(options.KeyPageUrl is { } page ? " (" + page + ")" : string.Empty)} — server {options.ServerName}, port {options.Port}, nick {TomYum.Irc.IrcLine.Nick(context.Caller.Presence.Username)}.");
        var password = await tokens.CreateAsync(context.Caller.UserId).ConfigureAwait(false);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Your IRC password: {password} — server {options.ServerName}, port {options.Port}, nick {TomYum.Irc.IrcLine.Nick(context.Caller.Presence.Username)}. Asking again makes a new one.");
        var writer = new BanchoPacketWriter(256);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, text, context.Caller.Presence.Username, BanchoBot.UserId));
        context.Caller.Enqueue(writer.ToArray());
        return "I sent you your IRC password in a private message.";
    }

    [ChatCommand("report", Usage = "<player> <reason>")]
    private Task<string> Report(ChatCommandContext context) => ReportAsync(context.Caller, context.Parts, context.CancellationToken);

    private async Task<string> LinkDiscordAsync(OnlinePlayer caller, string[] parts, CancellationToken cancellationToken)
    {
        if (_discordCodes is null)
            return "There's no Discord bot on this server.";
        if (parts.Length < 2)
            return "Use /link in our Discord to get a code, then send !link <code> here.";
        if (_discordCodes.Redeem(parts[1]) is not { } discordId)
            return "That code is wrong or expired. Get a new one with /link in Discord.";

        using (var scope = _scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDiscordLinkRepository>()
                .LinkAsync(caller.UserId, discordId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        await _discordCodes.OnLinkedAsync(caller.UserId, discordId).ConfigureAwait(false);
        return "Linked to Discord! Your roles will show up there in a moment.";
    }

    /// <summary>Report reasons as players type them, and the website's name for each.</summary>
    private static readonly Dictionary<string, string> ReportReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cheating"] = "Cheating", ["cheat"] = "Cheating", ["cheats"] = "Cheating", ["hacking"] = "Cheating",
        ["multi"] = "MultipleAccounts", ["multiaccount"] = "MultipleAccounts", ["multiaccounting"] = "MultipleAccounts", ["alt"] = "MultipleAccounts",
        ["insults"] = "Insults", ["insult"] = "Insults", ["toxic"] = "Insults",
        ["spam"] = "Spam", ["chat"] = "InappropriateChat", ["inappropriate"] = "InappropriateChat",
        ["other"] = "Other",
    };

    /// <summary>
    /// <c>!report &lt;player&gt; &lt;reason&gt; [details]</c>: files a report like the website's (staff
    /// see it in the GMT panel and on Discord). A few per hour at most.
    /// </summary>
    private async Task<string> ReportAsync(OnlinePlayer caller, string[] parts, CancellationToken cancellationToken)
    {
        const string usage = "Usage: !report <player> <cheating|multi|insults|spam|chat|other> [what happened]";
        if (parts.Length < 3)
            return usage;

        using var scope = _scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<TomYum.Persistence.OsuWeb.OsuWebCommunity>() is not { } community)
            return "Reports go through the website, which this server doesn't have.";
        var target = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .GetBySafeNameAsync(User.MakeSafeName(parts[1]), cancellationToken).ConfigureAwait(false);
        if (target is null)
            return $"No such player \"{parts[1]}\".";
        if (target.Id == caller.UserId || target.Id == BanchoBot.UserId)
            return "You can't report that account.";

        var known = ReportReasons.TryGetValue(parts[2], out var reason);
        var details = string.Join(' ', parts[(known ? 3 : 2)..]);
        if (await community.CountReportsByAsync(caller.UserId, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false) >= 5)
            return "You've sent a lot of reports this hour; please wait a bit.";

        var comments = details.Length > 2000 ? details[..2000] : details;
        await community.AddReportAsync(caller.UserId, target.Id, reason ?? "Other", comments + " (sent in-game)", cancellationToken).ConfigureAwait(false);
        return $"Thanks, your report on {target.Username} went to the staff.";
    }

    /// <summary>Lists loaded plugins (the <c>!plugins</c> command).</summary>
    private string ListPlugins()
    {
        var loaded = _loadedPlugins.Loaded;
        if (loaded.Count == 0)
            return "No plugins are loaded.";

        var entries = loaded.Select(m =>
            string.Create(CultureInfo.InvariantCulture, $"{m.Name} v{m.Version} ({m.Id}) by {m.Authors}"));
        return string.Create(CultureInfo.InvariantCulture, $"Loaded plugins ({loaded.Count}): ")
            + string.Join(" | ", entries);
    }

    /// <summary>
    /// Routes a command the built-in switch didn't handle to a plugin-registered command. Returns the
    /// plugin's reply (which may be <c>null</c> = handled with no reply), or the "unknown command"
    /// message when no plugin owns it.
    /// </summary>
    private async Task<string?> FallbackToPluginAsync(
        OnlinePlayer caller,
        string command,
        string[] parts,
        string rawText,
        CancellationToken cancellationToken)
    {
        if (_pluginCommands.TryGet(command, out var pluginCommand) && pluginCommand is not null)
        {
            var arguments = parts.Length > 1 ? parts[1..] : [];
            var invocation = new CommandInvocation(command, arguments, rawText, caller.UserId, Channel: null);
            return await pluginCommand.Handler(invocation, cancellationToken).ConfigureAwait(false);
        }

        return string.Create(CultureInfo.InvariantCulture, $"Unknown command \"{command}\". Try !help.");
    }

    private const string StaffOnly = "That command is staff-only.";

    private static bool IsStaff(BanchoPrivileges privileges) =>
        privileges.HasFlag(BanchoPrivileges.Owner) || privileges.HasFlag(BanchoPrivileges.Developer);

    private static string Roll(string sender, string[] parts)
    {
        var max = 100;
        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) && requested > 0)
            max = requested;

        var value = Random.Shared.Next(1, max + 1);
        return string.Create(CultureInfo.InvariantCulture, $"{sender} rolls {value} point(s)! (1-{max})");
    }

    private string Stats()
    {
        var players = _players.All;
        long queuedBytes = 0;
        foreach (var player in players)
            queuedBytes += player.QueuedBytes;

        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);

        // Build identity + the runtime/host the process is actually running on (staff diagnostics).
        var build = $"TomYum {_version.Display} @{_version.Commit}";
        var host = $"{RuntimeInformation.FrameworkDescription} | host {Environment.MachineName} | " +
                   $"{OsSummary()} {UnameArch(RuntimeInformation.OSArchitecture)} ({Environment.ProcessorCount} cpu) up {ProcessUptime()}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{build} | {host} | Players: {players.Count} | Queued: {queuedBytes / 1024.0:F1} KB | Channels: {_channels.All.Count} | Beatmaps cached: {_beatmaps.CachedCount} | Heap: {heapMb:F1} MB");
    }

    /// <summary>
    /// The OS product plus its kernel/build release: <c>RuntimeInformation.OSDescription</c> (the distro
    /// or Windows product) with the kernel release from <c>Environment.OSVersion</c> appended when it
    /// isn't already part of the description (Linux reports the distro but not the kernel; Windows
    /// already embeds the build). E.g. <c>Debian GNU/Linux 13 (trixie) (kernel 6.12.94)</c> or
    /// <c>Microsoft Windows 10.0.26100</c>.
    /// </summary>
    private static string OsSummary()
    {
        var description = RuntimeInformation.OSDescription.Trim();
        var version = Environment.OSVersion.Version;
        var release = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");
        var label = Environment.OSVersion.Platform == PlatformID.Win32NT ? "NT" : "kernel";

        return description.Contains(release, StringComparison.Ordinal)
            ? description
            : $"{description} ({label} {release})";
    }

    /// <summary>Maps the runtime architecture to its conventional uname spelling (<c>X64</c> → <c>x86_64</c>).</summary>
    private static string UnameArch(Architecture arch) => arch switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "aarch64",
        Architecture.Arm => "arm",
        _ => arch.ToString().ToLowerInvariant(),
    };

    /// <summary>How long the server process has been running, as a compact <c>1d2h3m</c>/<c>2h3m</c>/<c>3m4s</c> string.</summary>
    private static string ProcessUptime()
    {
        using var process = Process.GetCurrentProcess();
        var up = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return up.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)up.TotalDays}d{up.Hours}h{up.Minutes}m")
            : up.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)up.TotalHours}h{up.Minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{up.Minutes}m{up.Seconds}s");
    }

    // ---- !mp subcommands ----

    private async Task<string?> MpAsync(
        OnlinePlayer caller,
        string[] parts,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
            return "Usage: !mp <start [seconds]|abort|countdown <seconds>|host <player>|kick <player>>";

        var sub = parts[1].ToLowerInvariant();

        var match = _matches.GetByUserId(caller.UserId);
        if (match is null)
            return "You are not in a multiplayer room.";

        // Only the host can control the match.
        if (sub is "start" or "abort" or "countdown" && match.HostUserId != caller.UserId && !IsStaff(privileges))
            return "Only the host can do that.";

        return sub switch
        {
            "start"     => await MpStartAsync(match, parts).ConfigureAwait(false),
            "abort"     => TomYum.Server.Admin.MatchModeration.Abort(match, _players) ? null : "Nothing to abort.",
            "countdown" => await MpCountdownAsync(match, parts).ConfigureAwait(false),
            "host"      => MpHost(match, parts, caller, privileges),
            "kick"      => MpKick(match, parts, privileges),
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown !mp subcommand \"{sub}\"."),
        };
    }

    private async Task<string?> MpStartAsync(Match match, string[] parts)
    {
        if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) && secs > 0)
            return await MpCountdownCoreAsync(match, secs, autoStart: true).ConfigureAwait(false);

        StartMatch(match);
        PostToMatch(match, "Good luck, have fun!");
        return null;
    }

    private async Task<string?> MpCountdownAsync(Match match, string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) || secs <= 0)
            return "Usage: !mp countdown <seconds>";

        return await MpCountdownCoreAsync(match, secs, autoStart: false).ConfigureAwait(false);
    }

    private async Task<string?> MpCountdownCoreAsync(Match match, int seconds, bool autoStart)
    {
        // Cancel any existing countdown first.
        match.CountdownCts?.Cancel();

        var cts = new CancellationTokenSource();
        match.CountdownCts = cts;

        var action = autoStart ? "starts" : "countdown ends";
        PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match {action} in {seconds} second{(seconds == 1 ? "" : "s")}!"));

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCountdownAsync(match, seconds, autoStart, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (match.CountdownCts == cts)
                    match.CountdownCts = null;
                cts.Dispose();
            }
        });

        return null;
    }

    private async Task RunCountdownAsync(Match match, int seconds, bool autoStart, CancellationToken token)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);

        // Early-warning milestones for long countdowns.
        foreach (var milestone in new[] { 60, 30 })
        {
            if (seconds <= milestone) continue;
            var delay = end - DateTime.UtcNow - TimeSpan.FromSeconds(milestone);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);
            PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match starts in {milestone} seconds!"));
        }

        // Per-second countdown for the last 10 seconds (or fewer if the total is short).
        var countFrom = Math.Min(seconds - 1, 10);
        for (var i = countFrom; i >= 1; i--)
        {
            var delay = end - DateTime.UtcNow - TimeSpan.FromSeconds(i);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);
            PostToMatch(match, string.Create(CultureInfo.InvariantCulture, $"Match starts in {i} second{(i == 1 ? "" : "s")}!"));
        }

        var finalDelay = end - DateTime.UtcNow;
        if (finalDelay > TimeSpan.Zero)
            await Task.Delay(finalDelay, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (autoStart)
        {
            StartMatch(match);
            PostToMatch(match, "Good luck, have fun!");
        }
        else
        {
            PostToMatch(match, "Countdown ended. Type !mp start to begin.");
        }
    }

    private void StartMatch(Match match)
    {
        lock (match.SyncRoot)
        {
            if (match.InProgress) return;
            match.InProgress = true;
            match.LoadedPlayers.Clear();
            match.SkipRequests.Clear();
            foreach (var s in match.Slots)
            {
                if (s.Status is SlotStatus.Ready or SlotStatus.NotReady)
                    s.Status = SlotStatus.Playing;
            }
        }

        var startBytes = MatchBroadcast.EncodeMatch(ServerPacketId.MatchStart, match, sendPassword: true);
        MatchBroadcast.ToMatch(match, _players, startBytes);

        var lobbyUpdate = MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false);
        MatchBroadcast.ToLobby(_players, lobbyUpdate);
    }

    private string MpHost(Match match, string[] parts, OnlinePlayer caller, BanchoPrivileges privileges)
    {
        if (!IsStaff(privileges) && match.HostUserId != caller.UserId)
            return "Only the host can transfer host.";
        if (parts.Length < 3)
            return "Usage: !mp host <username>";

        var targetName = parts[2];
        var target = _players.GetByUsername(targetName);
        if (target is null)
            return string.Create(CultureInfo.InvariantCulture, $"\"{targetName}\" is not online.");

        lock (match.SyncRoot)
        {
            if (match.IndexOf(target.UserId) < 0)
                return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is not in this room.");
            match.HostUserId = target.UserId;
        }

        MatchBroadcast.UpdateMatch(match, _players);
        return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is now the host.");
    }

    private string MpKick(Match match, string[] parts, BanchoPrivileges privileges)
    {
        if (parts.Length < 3)
            return "Usage: !mp kick <username>";

        var targetName = parts[2];
        var target = _players.GetByUsername(targetName);
        if (target is null)
            return string.Create(CultureInfo.InvariantCulture, $"\"{targetName}\" is not online.");

        lock (match.SyncRoot)
        {
            var slotIdx = match.IndexOf(target.UserId);
            if (slotIdx < 0)
                return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} is not in this room.");
            match.Slots[slotIdx].Reset();
        }

        match.Channel.Leave(target.UserId);
        target.LeaveChannel("#multiplayer");

        // Tell the kicked player they've been removed.
        target.Enqueue(new BanchoPacketWriter(16).WritePacket(ServerPacketId.MatchJoinFail, null).ToArray());

        MatchBroadcast.UpdateMatch(match, _players);
        return string.Create(CultureInfo.InvariantCulture, $"{target.Presence.Username} was kicked from the room.");
    }

    /// <summary>Posts a bot message directly into the match's per-room channel.</summary>
    private static void PostToMatch(Match match, string text)
    {
        var w = new BanchoPacketWriter(128);
        ChatEncoder.WriteMessage(w, new BanchoMessage(BanchoBot.Name, text, "#multiplayer", BanchoBot.UserId));
        var bytes = w.ToArray();
        foreach (var member in match.Channel.Members)
            member.Enqueue(bytes);
    }

    // ---- !setpriv ----

    private async Task<string> SetPrivAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 3)
            return "Usage: !setpriv <username> <owner|admin|dev|mod|supporter|normal>";

        if (!TryParseRole(parts[2], out var privileges))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Unknown role \"{parts[2]}\". Valid: owner, admin, dev, mod, supporter, normal.");
        }

        var targetName = parts[1];
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var user = await users.GetBySafeNameAsync(User.MakeSafeName(targetName), cancellationToken).ConfigureAwait(false);
        if (user is null)
            return string.Create(CultureInfo.InvariantCulture, $"No such user \"{targetName}\".");

        await users.UpdateAsync(WithPrivileges(user, privileges), cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture,
            $"{user.Username} is now {privileges}. (Takes effect on their next login.)");
    }

    private static bool TryParseRole(string role, out UserPrivileges privileges)
    {
        privileges = role.ToLowerInvariant() switch
        {
            "owner" => UserPrivileges.Owner,
            "admin" or "administrator" => UserPrivileges.Normal | UserPrivileges.Supporter | UserPrivileges.Moderator | UserPrivileges.Administrator,
            "dev" or "developer" => UserPrivileges.Normal | UserPrivileges.Supporter | UserPrivileges.Moderator | UserPrivileges.Administrator | UserPrivileges.Developer,
            "mod" or "moderator" => UserPrivileges.Normal | UserPrivileges.Moderator,
            "supporter" => UserPrivileges.Normal | UserPrivileges.Supporter,
            "normal" or "player" => UserPrivileges.Normal,
            _ => UserPrivileges.None,
        };
        return privileges != UserPrivileges.None;
    }

    private static User WithPrivileges(User user, UserPrivileges privileges) => new()
    {
        Id             = user.Id,
        Username       = user.Username,
        SafeName       = user.SafeName,
        Email          = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country        = user.Country,
        CreationTime   = user.CreationTime,
        LatestActivity = user.LatestActivity,
        ApiKey         = user.ApiKey,
        Privileges     = privileges,
    };
}
