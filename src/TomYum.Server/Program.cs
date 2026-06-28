using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration: appsettings.json + appsettings.{Environment}.json + env vars
// (see ADR-0008). The optional tomyum.yaml overlay is wired in when the config library lands.
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

// --- Bancho (osu!stable) online state + login ---
// In-memory single-node services for the first vertical slice. Persistence (MySQL) and
// cross-node online state (Redis, ADR-0003) replace the in-memory pieces in later slices.
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddScoped<BanchoLoginHandler>();

var app = builder.Build();

app.Logger.LogInformation("TomYum starting — environment: {Environment}", app.Environment.EnvironmentName);

// Seed a test player so the login handshake can be exercised end to end until the real
// user table lands. TODO: remove once TomYum.Persistence backs IUserRepository.
if (app.Environment.IsDevelopment())
{
    var users = app.Services.GetRequiredService<IUserRepository>();
    await users.AddAsync(new User { Id = 2, Username = "TomYum" });
}

// Liveness probe for k8s/k3s.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Server" }));

// osu!stable bancho surface: POST / login + poll, GET / status page.
app.MapBancho();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry assembly.</summary>
public partial class Program;
