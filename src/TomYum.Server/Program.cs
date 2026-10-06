using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Gateway;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Match;
using TomYum.Server.Bancho.Packets.Spectate;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;
using TomYum.Server.Plugins;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration (ADR-0008): the committed defaults live under config/ at the repo
// root, then appsettings.{Environment}.json overrides, then env vars (TOMYUM_Section__Key)
// win last. The config/ directory is not under the content root (the project folder) nor
// reliably under the CWD (dotnet run sets CWD to the project dir), so walk up from the
// content root until a config/ directory is found.
static string ResolveConfigDir(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "config");
        if (Directory.Exists(candidate))
            return candidate;
    }
    return Path.Combine(start, "config");
}

var configDir = ResolveConfigDir(builder.Environment.ContentRootPath);

// Load .env from the config/ directory if present (local dev / docker-compose).
// Environment variables already set in the process always win (docker -e, k8s secret mounts).
var dotEnvPath = Path.Combine(configDir, ".env");
if (File.Exists(dotEnvPath))
    DotNetEnv.Env.Load(dotEnvPath);

builder.Configuration
    .AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(
        Path.Combine(configDir, $"appsettings.{builder.Environment.EnvironmentName}.json"),
        optional: true,
        reloadOnChange: true)
    .AddYamlFile(Path.Combine(configDir, "tomyum.yaml"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

// Every SQL statement is logged at Information; outside development that buries the log (the
// Discord announcer alone queries every few seconds). Failed commands are still logged as errors.
if (!builder.Environment.IsDevelopment())
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// --- Bancho (osu!stable) online state + auth ---
// In-memory single-node services for this vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<ChannelRegistry>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddSingleton<TomYum.Server.Admin.MatchModeration>();
builder.Services.AddSingleton<TomYumVersion>();
builder.Services.AddSingleton<ModerationCommands>();
builder.Services.AddSingleton<MapCommands>();
builder.Services.AddSingleton<NowPlayingStore>();
builder.Services.AddSingleton<TomYum.Server.OsuWeb.OnlinePlayers>();
builder.Services.AddSingleton<BanchoBotCommands>();
builder.Services.AddSingleton<BanchoBot>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.TryAddSingleton(TimeProvider.System);
// Restricted players (kept off leaderboards/ranks) and the in-game chat log for staff.
builder.Services.AddSingleton<RestrictedUsers>();
// Maintenance mode (only staff log in) and in-game alerts, from the admin panel.
builder.Services.AddSingleton(sp => new Maintenance(sp.GetRequiredService<PlayerRegistry>(), sp.GetRequiredService<IServiceScopeFactory>(),
    TimeProvider.System, sp.GetRequiredService<ILogger<Maintenance>>(), sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
    sp.GetService<OsuWebChatBridge>(), sp.GetService<ChannelRegistry>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<Maintenance>());
builder.Services.AddSingleton<ChatLog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatLog>());
// pp reworks (every score in some modes worked out again; their rankings wait), from the admin panel.
builder.Services.AddSingleton(sp => new PpReworks(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<PpReworks>>(), TimeProvider.System));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PpReworks>());
builder.Services.AddSingleton(sp => new PpLibraries(sp.GetService<StackExchange.Redis.IConnectionMultiplexer>()));

// User/beatmap persistence: MySQL (EF Core) when a Database section is configured and enabled,
// otherwise the throwaway in-memory repositories so the server still runs without a database
// (local dev, tests). The EF repositories are scoped; the in-memory ones are singletons.
var databaseOptions = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
builder.Services.AddSingleton(databaseOptions); // shared with the plugin host (trusted data access)
if (databaseOptions.Enabled)
{
    builder.Services.AddTomYumPersistence(databaseOptions);
}
else
{
    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
    builder.Services.AddSingleton<IScoreRepository, InMemoryScoreRepository>();
    builder.Services.AddSingleton<IUserStatsRepository, InMemoryUserStatsRepository>();
    builder.Services.AddSingleton<IUserAchievementRepository, InMemoryUserAchievementRepository>();
    builder.Services.AddSingleton<IClientHashRepository, InMemoryClientHashRepository>();
    builder.Services.AddSingleton<IDiscordLinkRepository, InMemoryDiscordLinkRepository>();
    builder.Services.AddSingleton<IBeatmapCommentRepository, TomYum.Server.Beatmaps.InMemoryBeatmapCommentRepository>();
}
// Friends live on the website with its accounts (phpbb_zebra); otherwise in memory.
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IFriendRepository, InMemoryFriendRepository>(builder.Services);
// Website sync (docs/osu-web.md): mirrors scores/stats to osu-web when accounts live there.
if (databaseOptions.UseOsuWebAccounts)
{
    builder.Services.AddSingleton(sp => new OsuWebScoreIndexing(
        sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), databaseOptions.OsuWebIndexPrefix));
    builder.Services.AddScoped<OsuWebMedals>();
    builder.Services.AddScoped<TomYum.Server.Achievements.MedalRebuild>();
    var interopOptions = new OsuWebInteropOptions();
    builder.Configuration.GetSection(OsuWebInteropOptions.SectionName).Bind(interopOptions);
    builder.Services.AddSingleton(interopOptions);
    builder.Services.AddHttpClient<OsuWebInterop>(http => http.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddScoped<OsuWebSync>();
    builder.Services.AddScoped<IOsuWebSync>(sp => sp.GetRequiredService<OsuWebSync>());
    builder.Services.AddScoped<TomYum.Server.Achievements.PackMedalAwarder>();
    // Profile rank graphs: a daily snapshot of everyone's rank.
    builder.Services.AddHostedService<OsuWebRankHistoryJob>();
    // Featured artist tags (and descriptions/nominators) for sets imported before imports brought them.
    builder.Services.AddHostedService<BeatmapsetExtrasBackfill>();
    // Lazer presence (TomYum's spectator build publishes it to Redis) and the website's online graph.
    builder.Services.AddSingleton(sp => new LazerPresence(
        sp.GetRequiredService<ILogger<LazerPresence>>(), sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
    builder.Services.AddHostedService<BanchoStatsJob>();
    // Ranked Play: how often each pool map was played (ppy's spectator reads it but never counts it).
    builder.Services.AddHostedService<MatchmakingSelectionCounts>();
    // The website's first places (profile "First Place Scores"), which nothing else keeps.
    builder.Services.AddHostedService<OsuWebLeadersJob>();
    // Score cards (Discord) show the star rating with the play's mods.
    builder.Services.AddSingleton<IStarRatings, ModStarRatings>();
    // Lazer's daily challenge: osu-web opens the rooms; TomYum keeps its queue filled.
    var dailyChallengeOptions = new DailyChallengeOptions();
    builder.Configuration.GetSection(DailyChallengeOptions.SectionName).Bind(dailyChallengeOptions);
    builder.Services.AddSingleton(dailyChallengeOptions);
    if (dailyChallengeOptions.Enabled)
        builder.Services.AddHostedService<DailyChallengeQueueJob>();

    // ppy's score processor next to osu-web (lazer support, docs/lazer.md): it owns pp on the website.
    var mirrorOptions = new OsuWebMirrorOptions();
    builder.Configuration.GetSection(OsuWebInteropOptions.SectionName).Bind(mirrorOptions);
    builder.Services.AddSingleton(mirrorOptions);
    if (mirrorOptions.ScoreProcessor)
    {
        builder.Services.AddSingleton(sp => new OsuWebScoreProcessing(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
        builder.Services.AddHostedService<OsuWebPerformanceWatcher>();
    }
    builder.Services.AddScoped<OsuWebPerformanceSync>();
    // Stable scores already on the website: into lazer's scoring, once (docs/lazer.md).
    builder.Services.AddHostedService<OsuWebScoreConversionBackfill>();

    // Admin API for osu-web's staff panels (signed with the same shared secret as InterOp).
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<AdminAuthFilter>();
    builder.Services.AddScoped<BeatmapSetImporter>();
    builder.Services.AddScoped<ScoreRecalculation>();
    builder.Services.AddScoped<BeatmapStatusChanger>();
    builder.Services.AddScoped<UserModerationSync>();
    // BanchoBot's PM when a player is restricted or unrestricted.
    builder.Services.AddSingleton(sp => new RestrictionNotices(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<PlayerRegistry>(),
        $"https://osu.{(BanchoHosts.ResolveDomain(builder.Configuration) is { Length: > 0 } noticeDomain ? noticeDomain : "localhost")}",
        sp.GetRequiredService<ILogger<RestrictionNotices>>(), sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetService<OsuWebChatBridge>()));
    // Website chat ↔ in-game chat.
    builder.Services.AddSingleton<OsuWebChatBridge>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OsuWebChatBridge>());
    // Recent activity "achieved rank #N" entries and #1s in #announce, for stable and lazer plays.
    builder.Services.AddHostedService<ScoreRankEvents>();
    // Custom sets nobody updated for four weeks go to the graveyard, as on osu!.
    builder.Services.AddHostedService<CustomGraveyardJob>();
    // Lazer's Relax plays on TomYum's relax leaderboards (ppy's processor doesn't rank them).
    builder.Services.AddHostedService<LazerRelaxImport>();
    builder.Services.AddHostedService<TagSync>();
    builder.Services.AddHostedService<WebsiteLastVisit>();
    builder.Services.AddHostedService<TomYum.Server.Admin.ModeRankHistoryJob>();
    builder.Services.AddScoped<ScoreModeration>();
    builder.Services.AddSingleton<BeatmapImportRunner>();
    // Beatmap submission from the game clients (stable's .osz2, lazer's submission service).
    builder.Services.AddScoped<BeatmapSubmissions>();
    builder.Services.AddSingleton<LazerTokens>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BeatmapImportRunner>());
}
else
{
    builder.Services.AddSingleton<IOsuWebSync, NoOsuWebSync>();
}
builder.Services.AddScoped<PresenceStats>();
builder.Services.AddScoped<BanchoLoginHandler>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddTomYumCaching(builder.Configuration);

// --- Beatmap status (osu! v1 API) ---
// Bind the Osu options (key from config/OSU_API_KEY env) and a typed HttpClient pointed at
// the keyed official endpoint or the keyless mirror. BeatmapService fronts the API with an
// in-memory cache. See memories/references/bancho.py.md for the resolution model.
var osuOptions = new OsuApiOptions();
builder.Configuration.GetSection(OsuApiOptions.SectionName).Bind(osuOptions);
builder.Services.AddSingleton(osuOptions);
// Everything asking osu!'s API shares one budget (osu! asks for at most 60 requests a minute);
// map lookups ask the beatmap mirror (catboy.best) first, so most never reach osu!.
builder.Services.AddSingleton(new OsuApiBudget(TimeProvider.System,
    builder.Configuration.GetValue("Osu:RequestsPerMinute", OsuApiBudget.DefaultPerMinute)));
builder.Services.AddTransient<OsuApiBudgetHandler>();
var beatmapMirrorOptions = new MirrorOptions();
builder.Configuration.GetSection(MirrorOptions.SectionName).Bind(beatmapMirrorOptions);
builder.Services.AddSingleton(beatmapMirrorOptions);
builder.Services.AddKeyedSingleton(BeatmapMirror.PaceKey, new OsuApiBudget(TimeProvider.System, beatmapMirrorOptions.RequestsPerMinute));
builder.Services.AddHttpClient<BeatmapMirror>(http =>
{
    http.BaseAddress = new Uri(beatmapMirrorOptions.Url.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(10);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(BeatmapMirror.UserAgentFor(beatmapMirrorOptions,
        $"https://osu.{(BanchoHosts.ResolveDomain(builder.Configuration) is { Length: > 0 } siteDomain ? siteDomain : "localhost")}"));
});
builder.Services.AddHttpClient<OsuApiClient>(http =>
{
    http.BaseAddress = new Uri(osuOptions.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<OsuApiBudgetHandler>();
builder.Services.AddTransient<IOsuApiClient, MirrorFirstOsuApiClient>();
builder.Services.AddSingleton<BeatmapService>();
// Custom (uploaded) beatmap sets: stored and served by this server (docs/custom-beatmaps.md).
builder.Services.AddSingleton<CustomBeatmapStore>();
// Custom sets stored before their previews were cut from their songs get one.
builder.Services.AddHostedService<CustomPreviewBackfill>();
// Uploaded maps get the BPM osu! gives (the one they are at the longest), not their first timing point's.
builder.Services.AddHostedService<CustomBpmBackfill>();
builder.Services.AddSingleton<CustomBeatmapCatalog>();
builder.Services.AddScoped<CustomBeatmapImporter>();
// osu! API v2 (descriptions, nominators) when an OAuth application is configured.
builder.Services.AddHttpClient<OsuApiV2Client>(http =>
{
    http.BaseAddress = new Uri("https://osu.ppy.sh/");
    http.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<OsuApiBudgetHandler>();

// --- Pluggable pp engine (ADR-0010) ---
// RoutingPerformanceCalculator dispatches per gamemode to the configured backend (in-process
// official ppy calc, or an http sidecar like akatsuki-pp-rs). Defaults to `official` for all modes.
builder.Services.AddTomYumPerformance(builder.Configuration);

// --- Plugin host (ADR-0007) ---
// Loads .dll + loose .cs plugins from the configured directory, gives each a scoped context
// (config/logger/events/commands/http/trusted-data), and drives their lifecycle. DAN Mode and
// Session ship as plugins on this host.
builder.Services.AddTomYumPlugins(builder.Configuration);

// --- osu!Direct beatmap browser ---
// Named HttpClient for the mirror; options let operators point at any mino.pw-compatible mirror.
builder.Services.Configure<DirectOptions>(builder.Configuration.GetSection(DirectOptions.Section));
builder.Services.Configure<TomYum.Server.Bancho.ClientVerificationOptions>(builder.Configuration.GetSection(TomYum.Server.Bancho.ClientVerificationOptions.Section));
builder.Services.AddHttpClient(DirectEndpoints.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});
// Downloads passed through to old clients (docs/old-clients.md): a whole .osz, so no short timeout.
builder.Services.AddHttpClient(DirectEndpoints.DownloadHttpClientName, http =>
{
    http.Timeout = TimeSpan.FromMinutes(10);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});

// Audio previews (b.<domain>/preview/{set}.mp3), kept on disk; catboy.best wants an app that names itself.
builder.Services.AddHttpClient(AudioPreviews.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0 (osu! private server; audio previews)");
});
builder.Services.AddSingleton<AudioPreviews>();
builder.Services.AddSingleton<AssetStore>();
builder.Services.AddSingleton<MenuBanners>();

// --- Static assets (medal icons) ---
// Named HttpClient used to lazily fetch medal icons from ppy's CDN for the assets.<domain> proxy.
builder.Services.AddHttpClient(AssetsEndpoints.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});

// Profile badge images for the Discord badge commands (links are fetched from public addresses only).
builder.Services.AddHttpClient(BadgeImages.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    ConnectCallback = BadgeImages.ConnectPublicAsync,
    MaxAutomaticRedirections = 3,
});
builder.Services.AddSingleton<BadgeImages>();

// Countries for players Cloudflare can't place (DB-IP's free database, refreshed monthly).
builder.Services.AddHttpClient(IpCountry.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromMinutes(2);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});
builder.Services.AddSingleton<IpCountry>();
if (builder.Configuration.GetValue("GeoIp:Download", true))
    builder.Services.AddHostedService<IpCountryUpdater>();

// Packet dispatch: one stateless handler per client packet id, discovered by the singleton
// dispatcher into a frozen lookup. Register each handler so DI enumerates them.
builder.Services.AddSingleton<IClientPacketHandler, PingHandler>();
builder.Services.AddSingleton<IClientPacketHandler, ChangeActionHandler>();
builder.Services.AddSingleton<IClientPacketHandler, RequestStatusUpdateHandler>();
builder.Services.AddSingleton<IClientPacketHandler, UserStatsRequestHandler>();
builder.Services.AddSingleton<IClientPacketHandler, UserPresenceRequestHandler>();
builder.Services.AddSingleton<IClientPacketHandler, UserPresenceRequestAllHandler>();
builder.Services.AddSingleton<IClientPacketHandler, LogoutHandler>();
builder.Services.AddSingleton<IClientPacketHandler, SendPublicMessageHandler>();
builder.Services.AddSingleton<IClientPacketHandler, SendPrivateMessageHandler>();
builder.Services.AddSingleton<IClientPacketHandler, ChannelJoinHandler>();
builder.Services.AddSingleton<IClientPacketHandler, ChannelPartHandler>();
builder.Services.AddSingleton<IClientPacketHandler, FriendAddHandler>();
builder.Services.AddSingleton<IClientPacketHandler, FriendRemoveHandler>();
builder.Services.AddSingleton<IClientPacketHandler, SetAwayMessageHandler>();
builder.Services.AddSingleton<IClientPacketHandler, ToggleBlockNonFriendDmsHandler>();
builder.Services.AddSingleton<IClientPacketHandler, ReceiveUpdatesHandler>();
builder.Services.AddSingleton<IClientPacketHandler, TournamentMatchInfoHandler>();
builder.Services.AddSingleton<IClientPacketHandler, TournamentJoinMatchChannelHandler>();
builder.Services.AddSingleton<IClientPacketHandler, TournamentLeaveMatchChannelHandler>();

// Multiplayer match handlers.
builder.Services.AddSingleton<IClientPacketHandler, JoinLobbyHandler>();
builder.Services.AddSingleton<IClientPacketHandler, PartLobbyHandler>();
builder.Services.AddSingleton<IClientPacketHandler, CreateMatchHandler>();
builder.Services.AddSingleton<IClientPacketHandler, JoinMatchHandler>();
builder.Services.AddSingleton<IClientPacketHandler, PartMatchHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchChangeSlotHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchReadyHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchNotReadyHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchLockHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchChangeSettingsHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchStartHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchLoadCompleteHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchNoBeatmapHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchHasBeatmapHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchScoreUpdateHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchFailedHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchCompleteHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchSkipRequestHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchChangeModsHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchChangeTeamHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchTransferHostHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchInviteHandler>();
// Old clients (docs/old-clients.md): song select's beatmap info, and their error reports.
builder.Services.AddSingleton<IClientPacketHandler, TomYum.Server.Bancho.Packets.BeatmapInfoRequestHandler>();
builder.Services.AddSingleton<IClientPacketHandler, TomYum.Server.Bancho.Packets.ErrorReportHandler>();
builder.Services.AddSingleton<IClientPacketHandler, MatchChangePasswordHandler>();

// Spectating handlers.
builder.Services.AddSingleton<IClientPacketHandler, StartSpectatingHandler>();
builder.Services.AddSingleton<IClientPacketHandler, StopSpectatingHandler>();
builder.Services.AddSingleton<IClientPacketHandler, SpectateFramesHandler>();
builder.Services.AddSingleton<IClientPacketHandler, CantSpectateHandler>();

builder.Services.AddSingleton<BanchoPacketDispatcher>();

// circleguard (sundeiii/lazer-circleguard): replay checks after each ranked pass, stable and lazer.
var circleguardOptions = new TomYum.Server.Anticheat.CircleguardOptions();
builder.Configuration.GetSection(TomYum.Server.Anticheat.CircleguardOptions.SectionName).Bind(circleguardOptions);
if (circleguardOptions.IsConfigured && databaseOptions.UseOsuWebAccounts)
{
    builder.Services.AddSingleton(circleguardOptions);
    builder.Services.AddHttpClient<TomYum.Server.Anticheat.CircleguardClient>(http => http.Timeout = TimeSpan.FromMinutes(2));
    builder.Services.AddHttpClient<TomYum.Server.Anticheat.OfficialReplays>(http => http.Timeout = TimeSpan.FromSeconds(60))
        .AddHttpMessageHandler<OsuApiBudgetHandler>();
    builder.Services.AddSingleton<TomYum.Server.Anticheat.CircleguardScanner>();
    builder.Services.AddHostedService<TomYum.Server.Anticheat.CircleguardWatch>();
}

// Last.fm: players who connect it (account settings on the website) get their plays scrobbled.
var lastFmOptions = new TomYum.Server.LastFm.LastFmOptions();
builder.Configuration.GetSection(TomYum.Server.LastFm.LastFmOptions.SectionName).Bind(lastFmOptions);
if (lastFmOptions.IsConfigured && databaseOptions.UseOsuWebAccounts)
{
    builder.Services.AddSingleton(lastFmOptions);
    builder.Services.AddHttpClient<TomYum.Server.LastFm.LastFmClient>(http =>
    {
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/0.1 (osu! private server)");
    });
    builder.Services.AddHostedService<TomYum.Server.LastFm.LastFmScrobbler>();
}

// Where players log in to the game from (staff see it on Discord's /user).
builder.Services.AddSingleton(sp => new LoginIps(TimeProvider.System, sp.GetService<StackExchange.Redis.IConnectionMultiplexer>()));

// IRC (osu!-style, port 6667): chat and bots from IRC clients; passwords are the website's IRC keys
// (account settings), or from BanchoBot's !irc without website accounts.
builder.Services.AddSingleton(sp => new TomYum.Server.Irc.IrcTokens(sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
    databaseOptions.UseOsuWebAccounts ? sp.GetRequiredService<IServiceScopeFactory>() : null));
var ircOptions = new TomYum.Server.Irc.IrcOptions();
builder.Configuration.GetSection(TomYum.Server.Irc.IrcOptions.SectionName).Bind(ircOptions);
if (string.IsNullOrWhiteSpace(ircOptions.ServerName))
    ircOptions.ServerName = "irc." + (BanchoHosts.ResolveDomain(builder.Configuration) is { Length: > 0 } ircDomain ? ircDomain : "localhost");
if (databaseOptions.UseOsuWebAccounts && ircOptions.KeyPageUrl is null && BanchoHosts.ResolveDomain(builder.Configuration) is { Length: > 0 } keyDomain)
    ircOptions.KeyPageUrl = $"https://osu.{keyDomain}/home/account/edit#legacy-api";
builder.Services.AddSingleton(ircOptions);
if (ircOptions.Enabled)
    builder.Services.AddHostedService<TomYum.Server.Irc.IrcServer>();

// Bancho over raw TCP for osu! builds up to b20130815 (docs/old-clients.md): off unless Bancho:Tcp:Enabled.
var tcpBanchoOptions = new TcpBanchoOptions();
builder.Configuration.GetSection(TcpBanchoOptions.Section).Bind(tcpBanchoOptions);
builder.Services.AddSingleton(tcpBanchoOptions);
if (tcpBanchoOptions.Enabled)
    builder.Services.AddHostedService<TcpBanchoServer>();

// TomYum.Gateway (docs/gateway.md): the edge for raw TCP (pre-2014 bancho, IRC), on this host or
// another. Gateway:Key is the secret shared with it.
var gatewayOptions = new TomYum.Server.Gateway.GatewayOptions();
builder.Configuration.GetSection(TomYum.Server.Gateway.GatewayOptions.SectionName).Bind(gatewayOptions);
builder.Services.AddSingleton(gatewayOptions);

// The Discord bot (docs/discord.md): runs when Discord:Token and Discord:GuildId are set.
var discordOptions = TomYum.Server.Discord.DiscordOptions.Read(builder.Configuration.GetSection(TomYum.Server.Discord.DiscordOptions.SectionName));
if (discordOptions.IsConfigured)
{
    TomYum.Server.Discord.DiscordFormat.UseGradeEmojis(discordOptions.GradeEmojis);
    var discordDomain = BanchoHosts.ResolveDomain(builder.Configuration);
    var discordErrors = new TomYum.Server.Discord.DiscordLogSink();
    builder.Logging.AddProvider(discordErrors);
    builder.Services.AddSingleton(discordOptions);
    builder.Services.AddSingleton(discordErrors);
    builder.Services.AddSingleton(new TomYum.Server.Discord.DiscordFormat(
        string.IsNullOrWhiteSpace(discordOptions.SiteUrl) ? $"https://osu.{discordDomain}" : discordOptions.SiteUrl,
        string.IsNullOrWhiteSpace(discordOptions.AvatarUrl) ? $"https://a.{discordDomain}" : discordOptions.AvatarUrl));
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordLinkCodes>();
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordBotService>();
    builder.Services.AddSingleton<TomYum.Server.Discord.IDiscordGateway>(sp => sp.GetRequiredService<TomYum.Server.Discord.DiscordBotService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TomYum.Server.Discord.DiscordBotService>());
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordCommands>();
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordChatRelay>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TomYum.Server.Discord.DiscordChatRelay>());
    builder.Services.AddSingleton(sp => new TomYum.Server.Discord.DiscordState(sp.GetService<StackExchange.Redis.IConnectionMultiplexer>()));
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordInteractions>();
    builder.Services.AddSingleton<TomYum.Server.Discord.DiscordWatch>();
    builder.Services.AddHostedService<TomYum.Server.Discord.DiscordAnnouncer>();
    builder.Services.AddHostedService<TomYum.Server.Discord.DiscordDaily>();
}

var app = builder.Build();

// The gateway's player address, before anything reads X-Real-IP; WebSockets for its IRC relay.
app.UseGatewayLink(gatewayOptions);
app.UseWebSockets();

var version = app.Services.GetRequiredService<TomYumVersion>();
app.Logger.LogInformation(
    "TomYum {Version} starting — environment: {Environment}",
    version.Display, app.Environment.EnvironmentName);

// Seed BanchoBot as a permanent virtual player: it joins every channel, shows in every
// user list, and is never reaped. It logs in here once at startup rather than over the wire.
var botPlayer = app.Services.GetRequiredService<PlayerRegistry>().Add(BanchoBot.CreatePresence());
foreach (var channel in app.Services.GetRequiredService<ChannelRegistry>().All)
{
    channel.Join(botPlayer);
    botPlayer.JoinChannel(channel.Name);
}

// When MySQL is enabled, bring the schema up to date before serving traffic: EF Core migrations
// (databases from before migrations are baselined first, see DatabaseMigrator).
if (databaseOptions.Enabled)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
    var applied = await db.MigrateAsync();

    // TomYum's own accounts: ids up to BanchoBot's are reserved, so sign-ups start after it.
    if (!databaseOptions.UseOsuWebAccounts)
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE users AUTO_INCREMENT = {BanchoBot.UserId + 1}");

    app.Logger.LogInformation("Database schema up to date ({Host}/{Name}); applied {Migrations}.",
        databaseOptions.Host, databaseOptions.Name, applied.Count == 0 ? "nothing" : string.Join(", ", applied));

    // Staff-set (frozen) statuses override the osu! API in-game.
    var beatmapService = app.Services.GetRequiredService<BeatmapService>();
    foreach (var frozen in await db.Beatmaps.AsNoTracking().Where(b => b.StatusFrozen).Select(b => new { b.Id, b.Status }).ToListAsync())
        beatmapService.SetStatusOverride(frozen.Id, frozen.Status);

    // Accounts live in osu-web (docs/osu-web.md): its migrations own that schema, so nothing is
    // created there except BanchoBot's account row. Fails fast if osu-web's groups aren't seeded.
    if (databaseOptions.UseOsuWebAccounts)
    {
        var osuWebUsers = scope.ServiceProvider.GetRequiredService<OsuWebUserRepository>();
        await osuWebUsers.EnsureBotAsync(BanchoBot.UserId, BanchoBot.Name);
        app.Logger.LogInformation("Accounts stored in osu-web database {OsuWebName}.", databaseOptions.OsuWebName);

        var restrictedIds = await osuWebUsers.GetRestrictedIdsAsync();
        app.Services.GetRequiredService<RestrictedUsers>().Load(restrictedIds);
        if (restrictedIds.Count > 0)
            app.Logger.LogInformation("{Count} restricted player(s) kept off leaderboards.", restrictedIds.Count);

        // Maps played before the osu-web link existed: copy the ones the website doesn't have yet.
        var knownBeatmaps = await db.Beatmaps.AsNoTracking().ToListAsync();
        var mirrored = await scope.ServiceProvider.GetRequiredService<OsuWebBeatmapMirror>().BackfillAsync(knownBeatmaps);
        if (mirrored > 0)
            app.Logger.LogInformation("Copied {Count} beatmap(s) to osu-web.", mirrored);

        // Records which TomYum score became which osu-web score (lives in TomYum's database).
        var scoreMirror = scope.ServiceProvider.GetRequiredService<OsuWebScoreMirror>();
        await scoreMirror.EnsureLinkTableAsync();
        // Stable play counts ("Most Played Beatmaps", "Play History", map/set counts) from TomYum's scores.
        var playCounts = await scoreMirror.RebuildPlayCountsAsync();
        if (playCounts > 0)
            app.Logger.LogInformation("osu-web play counts brought up to date for {Count} player/beatmap pair(s).", playCounts);
        // Replays: website scores copied before replays were linked.
        var replaysMarked = await scoreMirror.MarkReplaysAsync(scoreId => ReplayFiles.Exists(app.Configuration, scoreId));
        if (replaysMarked > 0)
            app.Logger.LogInformation("Marked {Count} website score(s) as having a replay.", replaysMarked);

        // Medals: osu-web's catalog starts empty, and profiles only show catalogued medals. It's
        // osu!'s own list (ppy's processor awards lazer medals by osu!'s ids); TomYum's when offline.
        var medals = scope.ServiceProvider.GetRequiredService<OsuWebMedals>();
        // Local packs clear of osu!'s pack numbers, which ppy's processor checks for pack medals.
        var movedPacks = await scope.ServiceProvider.GetRequiredService<OsuWebPackCompletion>()
            .MovePacksOutOfOsusNumbersAsync(TomYum.Server.Achievements.PackMedalAwarder.LocalPackFloor);
        if (movedPacks > 0)
            app.Logger.LogInformation("Renumbered {Count} beatmap pack(s) clear of osu!'s pack numbers.", movedPacks);
        using (var medalHttp = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient())
        {
            medalHttp.Timeout = TimeSpan.FromSeconds(20);
            var official = await OsuWebMedals.FetchOfficialListAsync(medalHttp);
            if (official is null)
                app.Logger.LogWarning("Couldn't read osu!'s medal list from osu.ppy.sh; the website's catalog gets TomYum's own medals for now.");
            var catalogued = await medals.WriteCatalogAsync(official ?? OsuWebMedals.CatalogRows(), complete: official is not null);
            if (catalogued > 0)
                app.Logger.LogInformation("osu-web medal catalog: {Count} medal(s) added or updated.", catalogued);
        }
        try
        {
            var rebuild = scope.ServiceProvider.GetRequiredService<TomYum.Server.Achievements.MedalRebuild>();
            if (await rebuild.RunIfPendingAsync() is { } rebuilt)
                app.Logger.LogInformation(
                    "Medals recalculated under osu!'s ids and rules: {Scores} score(s) judged, {Added} medal(s) given, {Removed} taken back, {Legacy} old website copies removed.",
                    rebuilt.Scores, rebuilt.Added, rebuilt.Removed, rebuilt.LegacyCopiesRemoved);
            if (await rebuild.RunLazerIfPendingAsync() is { } lazer)
                app.Logger.LogInformation(
                    "Lazer and pack medals recalculated: {Scores} lazer play(s) judged, {Added} given, {Removed} taken back, {Packs} pack medal(s), {Notifications} old notification(s) removed.",
                    lazer.LazerScores, lazer.Added, lazer.Removed, lazer.PackMedals, lazer.Notifications);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogError(ex, "Recalculating medals failed; it runs again at the next start.");
        }
        // Country rankings for everyone with stats (lazer plays before this didn't update them).
        var ranked = await scope.ServiceProvider.GetRequiredService<OsuWebDbContext>().Database
            .SqlQuery<int>($"SELECT DISTINCT CAST(user_id AS SIGNED) AS Value FROM osu_user_stats UNION SELECT CAST(user_id AS SIGNED) FROM osu_user_stats_taiko UNION SELECT CAST(user_id AS SIGNED) FROM osu_user_stats_fruits UNION SELECT CAST(user_id AS SIGNED) FROM osu_user_stats_mania")
            .ToListAsync();
        await scope.ServiceProvider.GetRequiredService<OsuWebSync>().RecalculateCountriesOfAsync(ranked);
        var copiedUnlocks = await medals.CopyUnlocksAsync();
        if (copiedUnlocks > 0)
            app.Logger.LogInformation("osu-web medals: {Unlocks} earlier unlock(s) copied.", copiedUnlocks);
        var staleEvents = await medals.RemoveStaleMedalEventsAsync();
        if (staleEvents > 0)
            app.Logger.LogInformation("osu-web activity: {Count} entry(ies) for medals players don't have removed.", staleEvents);
        // Difficulties' file names (stable finds outdated maps and its song select statuses by them).
        var named = await scope.ServiceProvider.GetRequiredService<OsuWebStableClient>().FillMissingFilenamesAsync();
        if (named > 0)
            app.Logger.LogInformation("osu-web beatmaps: {Count} file name(s) filled in.", named);
        if (!scope.ServiceProvider.GetRequiredService<OsuWebInteropOptions>().IsConfigured)
            app.Logger.LogInformation("osu-web InterOp not configured (OsuWeb:InteropUrl/InteropSecret): medals are written directly, without activity entries.");
    }
}

// Seed a development test player (password "faker123") so the login handshake can be exercised
// end to end. Resolved through a scope so it works whether the repo is scoped (EF) or singleton
// (in-memory). Skipped if the account already exists, so it is safe across restarts with MySQL.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    const string devEmail = "tomyum@tomyum.local";
    // Also check the email: the account may have been renamed on the website since.
    if (!await users.SafeNameExistsAsync(User.MakeSafeName("TomYum")) && !await users.EmailExistsAsync(devEmail))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var bcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("faker123"));
        // Explicit id 2 keeps the id scheme aligned: 2 = this dev user, 3 = BanchoBot (virtual),
        // 4 = first real registration (granted Owner). MySQL accepts an explicit value
        // for an AUTO_INCREMENT key and advances the counter past it. With osu-web accounts the
        // ids are shared with website sign-ups, so let osu-web assign it (2 may already be taken).
        await users.AddAsync(new User
        {
            Id = databaseOptions.UseOsuWebAccounts ? 0 : 2,
            Username = "TomYum",
            SafeName = User.MakeSafeName("TomYum"),
            Email = devEmail,
            PasswordBcrypt = bcrypt,
            Country = "th",
        });
    }
}

// Admin API bodies are signed; keep them re-readable for the signature check.
if (databaseOptions.UseOsuWebAccounts)
    app.UseAdminRequestBuffering();

// Plugin-contributed HTTP routes (Session's frontend, etc.) — served before the bancho/web
// endpoints; non-matching requests pass straight through.
app.UsePluginRoutes(app.Services.GetRequiredService<PluginRouteTable>());

// Liveness probe for k8s/k3s.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Server", version = version.Display }));

// Host-aware routing: the osu!stable client splits its surface across subdomains —
// c/c4/ce/cho carry the bancho binary protocol, osu serves /web/* + registration.
// Patterns derive from Server:Domain; localhost is always included for dev. See
// memories/protocols/bancho-stable.md §1.
var domain = BanchoHosts.ResolveDomain(app.Configuration);
app.Logger.LogInformation("Bancho hosts bound to domain: {Domain}", domain);

// osu!stable bancho surface: POST / login + poll, GET / status page (c./c4./ce./cho.).
app.MapBancho(BanchoHosts.Bancho(domain));

// TCP bancho and IRC connections the gateway relays (docs/gateway.md); off without Gateway:Key.
if (gatewayOptions.Enabled)
{
    app.MapGatewayBancho(BanchoHosts.Bancho(domain));
    app.MapGatewayIrc(BanchoHosts.Bancho(domain));
}

// osu!stable in-game registration: POST /users (osu. host).
app.MapRegistration(BanchoHosts.Web(domain));

// osu!stable /web/*.php surface (osu. host): beatmap status via the osu! v1 API for now.
app.MapWeb(BanchoHosts.Web(domain));

// osu!Direct in-game beatmap browser: search, set-info, and download redirect.
app.MapDirect(BanchoHosts.Web(domain));
app.MapStableClient(BanchoHosts.Web(domain));

// Beatmap assets (b. subdomain): cover thumbnails for osu!Direct.
app.MapBeatmapAssets(BanchoHosts.Beatmap(domain));

// Static assets (assets. subdomain): medal icons for achievement-unlock popups.
app.MapAssets(BanchoHosts.Assets(domain));
app.MapStaticAssets(BanchoHosts.Static(domain));

// Admin API for osu-web's staff panels: internal host names only (Admin:Hosts).
if (databaseOptions.UseOsuWebAccounts)
{
    app.MapAdmin(app.Configuration.GetSection("Admin:Hosts").Get<string[]>() is { Length: > 0 } adminHosts
        ? adminHosts
        : AdminEndpoints.DefaultHosts);
    // Beatmap set uploads from the BN panel (a ticket signed by the website).
    app.MapCustomUploads(BanchoHosts.Web(domain));
    // Beatmap submission from the stable editor and lazer (docs/custom-beatmaps.md).
    app.MapSubmission(BanchoHosts.Web(domain));
}

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
