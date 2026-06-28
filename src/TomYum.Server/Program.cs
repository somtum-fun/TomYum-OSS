using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration: appsettings.json + appsettings.{Environment}.json + env vars
// (see ADR-0008). The optional tomyum.yaml overlay is wired in when the config library lands.
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

// --- Bancho (osu!stable) online state + auth ---
// In-memory single-node services for this vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<ChannelRegistry>();
builder.Services.AddSingleton<TomYumVersion>();
builder.Services.AddSingleton<BanchoBot>();
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<BanchoLoginHandler>();
builder.Services.AddScoped<RegistrationService>();

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

// Seed a test player (password "faker123") so the login handshake can be exercised end to
// end until the real user table lands. TODO: remove once TomYum.Persistence backs the repo.
if (app.Environment.IsDevelopment())
{
    var users = app.Services.GetRequiredService<IUserRepository>();
    var hasher = app.Services.GetRequiredService<IPasswordHasher>();
    var bcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("faker123"));
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

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
