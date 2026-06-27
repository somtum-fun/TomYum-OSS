var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Logger.LogInformation("TomYum.BeatmapSubmission starting (scaffold) — custom set_id 100000000+ flow TODO. See ADR-0009.");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.BeatmapSubmission" }));

app.Run();
