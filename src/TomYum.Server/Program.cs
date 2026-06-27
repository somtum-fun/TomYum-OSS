var builder = WebApplication.CreateBuilder(args);

// Layered configuration: appsettings.json + appsettings.{Environment}.json + env vars
// (see ADR-0008). The optional tomyum.yaml overlay is wired in when the config library lands.
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

var app = builder.Build();

app.Logger.LogInformation("TomYum starting — environment: {Environment}", app.Environment.EnvironmentName);

// Liveness probe for k8s/k3s. No protocol handling yet — this host only proves the
// foundation is runnable (scaffolding phase). See memories/progress.md.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Server" }));

app.Run();
