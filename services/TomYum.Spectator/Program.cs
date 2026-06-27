var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Logger.LogInformation("TomYum.Spectator starting (scaffold) — SignalR spectator hub TODO. See ADR-0003.");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Spectator" }));

app.Run();
