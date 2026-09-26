var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Logger.LogInformation("TomYum.ReplayStore starting (scaffold) — replay storage/retrieval TODO. See ADR-0006.");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.ReplayStore" }));

app.Run();
