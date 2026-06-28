using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration: appsettings.json + appsettings.{Environment}.json + env vars
// (see ADR-0008). The optional tomyum.yaml overlay is wired in when the config library lands.
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

// --- Bancho (osu!stable) online state + auth ---
// In-memory single-node services for this vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<BanchoLoginHandler>();
builder.Services.AddScoped<RegistrationService>();

var app = builder.Build();

app.Logger.LogInformation("TomYum starting — environment: {Environment}", app.Environment.EnvironmentName);

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
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Server" }));

// osu!stable bancho surface: POST / login + poll, GET / status page.
app.MapBancho();

// osu!stable in-game registration: POST /users (served on the osu. host in production).
app.MapRegistration();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
