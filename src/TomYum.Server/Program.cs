using Microsoft.EntityFrameworkCore;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
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

// --- Bancho (osu!stable) online state + auth ---
// In-memory single-node services for this vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<ChannelRegistry>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddSingleton<TomYumVersion>();
builder.Services.AddSingleton<BanchoBotCommands>();
builder.Services.AddSingleton<BanchoBot>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

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
}
// Website sync (docs/osu-web.md): mirrors scores/stats to osu-web when accounts live there.
if (databaseOptions.UseOsuWebAccounts)
{
    builder.Services.AddSingleton<OsuWebScoreIndexing>();
    builder.Services.AddScoped<IOsuWebSync, OsuWebSync>();
}
else
{
    builder.Services.AddSingleton<IOsuWebSync, NoOsuWebSync>();
}
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
builder.Services.AddHttpClient<IOsuApiClient, OsuApiClient>(http =>
{
    http.BaseAddress = new Uri(osuOptions.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<BeatmapService>();

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
builder.Services.AddHttpClient(DirectEndpoints.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});

// --- Static assets (medal icons) ---
// Named HttpClient used to lazily fetch medal icons from ppy's CDN for the assets.<domain> proxy.
builder.Services.AddHttpClient(AssetsEndpoints.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum/1.0");
});

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
builder.Services.AddSingleton<IClientPacketHandler, MatchChangePasswordHandler>();

// Spectating handlers.
builder.Services.AddSingleton<IClientPacketHandler, StartSpectatingHandler>();
builder.Services.AddSingleton<IClientPacketHandler, StopSpectatingHandler>();
builder.Services.AddSingleton<IClientPacketHandler, SpectateFramesHandler>();
builder.Services.AddSingleton<IClientPacketHandler, CantSpectateHandler>();

builder.Services.AddSingleton<BanchoPacketDispatcher>();

var app = builder.Build();

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

// When MySQL is enabled, ensure the schema exists before serving traffic. EnsureCreated is
// enough for the current single-version schema; switch to db.Database.MigrateAsync() once
// versioned migrations land so existing data survives schema changes.
if (databaseOptions.Enabled)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
    await db.Database.EnsureCreatedAsync();
    // Bridge tables added after the DB was first created (EnsureCreated won't add them). See SchemaGuard.
    await db.EnsureRuntimeTablesAsync();

    app.Logger.LogInformation("Database schema ensured ({Host}/{Name}).", databaseOptions.Host, databaseOptions.Name);

    // Accounts live in osu-web (docs/osu-web.md): its migrations own that schema, so nothing is
    // created there except BanchoBot's account row. Fails fast if osu-web's groups aren't seeded.
    if (databaseOptions.UseOsuWebAccounts)
    {
        await scope.ServiceProvider.GetRequiredService<OsuWebUserRepository>()
            .EnsureBotAsync(BanchoBot.UserId, BanchoBot.Name);
        app.Logger.LogInformation("Accounts stored in osu-web database {OsuWebName}.", databaseOptions.OsuWebName);

        // Maps played before the osu-web link existed: copy the ones the website doesn't have yet.
        var knownBeatmaps = await db.Beatmaps.AsNoTracking().ToListAsync();
        var mirrored = await scope.ServiceProvider.GetRequiredService<OsuWebBeatmapMirror>().BackfillAsync(knownBeatmaps);
        if (mirrored > 0)
            app.Logger.LogInformation("Copied {Count} beatmap(s) to osu-web.", mirrored);

        // Records which TomYum score became which osu-web score (lives in TomYum's database).
        await scope.ServiceProvider.GetRequiredService<OsuWebScoreMirror>().EnsureLinkTableAsync();
    }
}

// Seed a development test player (password "faker123") so the login handshake can be exercised
// end to end. Resolved through a scope so it works whether the repo is scoped (EF) or singleton
// (in-memory). Skipped if the account already exists, so it is safe across restarts with MySQL.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    if (!await users.SafeNameExistsAsync(User.MakeSafeName("TomYum")))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var bcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("faker123"));
        // Explicit id 2 keeps the id scheme aligned: 1 = BanchoBot (virtual), 2 = this dev
        // user, 3 = first real registration (granted Owner). MySQL accepts an explicit value
        // for an AUTO_INCREMENT key and advances the counter past it. With osu-web accounts the
        // ids are shared with website sign-ups, so let osu-web assign it (2 may already be taken).
        await users.AddAsync(new User
        {
            Id = databaseOptions.UseOsuWebAccounts ? 0 : 2,
            Username = "TomYum",
            SafeName = User.MakeSafeName("TomYum"),
            Email = "tomyum@tomyum.local",
            PasswordBcrypt = bcrypt,
            Country = "th",
        });
    }
}

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

// osu!stable in-game registration: POST /users (osu. host).
app.MapRegistration(BanchoHosts.Web(domain));

// osu!stable /web/*.php surface (osu. host): beatmap status via the osu! v1 API for now.
app.MapWeb(BanchoHosts.Web(domain));

// osu!Direct in-game beatmap browser: search, set-info, and download redirect.
app.MapDirect(BanchoHosts.Web(domain));

// Beatmap assets (b. subdomain): cover thumbnails for osu!Direct.
app.MapBeatmapAssets(BanchoHosts.Beatmap(domain));

// Static assets (assets. subdomain): medal icons for achievement-unlock popups.
app.MapAssets(BanchoHosts.Assets(domain));

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
