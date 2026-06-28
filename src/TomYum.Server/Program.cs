using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;

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
        {
            return candidate;
        }
    }

    // Fall back to a config/ beside the content root; AddJsonFile treats missing files as optional.
    return Path.Combine(start, "config");
}

var configDir = ResolveConfigDir(builder.Environment.ContentRootPath);
builder.Configuration
    .AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(
        Path.Combine(configDir, $"appsettings.{builder.Environment.EnvironmentName}.json"),
        optional: true,
        reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

// --- Bancho (osu!stable) online state + auth ---
// In-memory single-node services for this vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<ChannelRegistry>();
builder.Services.AddSingleton<TomYumVersion>();
builder.Services.AddSingleton<BanchoBotCommands>();
builder.Services.AddSingleton<BanchoBot>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

// User/beatmap persistence: MySQL (EF Core) when a Database section is configured and enabled,
// otherwise the throwaway in-memory repositories so the server still runs without a database
// (local dev, tests). The EF repositories are scoped; the in-memory ones are singletons.
var databaseOptions = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
if (databaseOptions.Enabled)
{
    builder.Services.AddTomYumPersistence(databaseOptions);
}
else
{
    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
}
builder.Services.AddScoped<BanchoLoginHandler>();
builder.Services.AddScoped<RegistrationService>();

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
    app.Logger.LogInformation("Database schema ensured ({Host}/{Name}).", databaseOptions.Host, databaseOptions.Name);
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
        // for an AUTO_INCREMENT key and advances the counter past it.
        await users.AddAsync(new User
        {
            Id = 2,
            Username = "TomYum",
            SafeName = User.MakeSafeName("TomYum"),
            Email = "tomyum@tomyum.local",
            PasswordBcrypt = bcrypt,
            Country = "th",
        });
    }
}

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

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
