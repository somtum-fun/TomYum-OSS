using System.Text.Json;
using TomYum.Plara;

// Pla-ra sidecar (docs/plara.md): POST /pp (a play's pp and stars), POST /stars (a map's stars), GET /version (what it
// runs: Pla-ra's version, osu!'s and the rulesets it rates), GET /health.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "9100"}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddHttpClient(PlaraService.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TomYum-Plara/1.0");
});
builder.Services.AddSingleton(new PlaraOptions(
    RulesetDirectory: Environment.GetEnvironmentVariable("PLARA_RULESET_DIR") ?? "data/plara-rulesets",
    OsuDirectory: Environment.GetEnvironmentVariable("OSU_CACHE_DIR") ?? "data/osu-cache"));
builder.Services.AddMemoryCache(o => o.SizeLimit = 200_000);
builder.Services.AddSingleton<PlaraService>();

var app = builder.Build();

app.MapGet("/health", () => "ok");
app.MapGet("/version", () => new
{
    Version = Somtum.Plara.PlaraProfiles.VERSION,
    OsuGame = typeof(osu.Game.OsuGameBase).Assembly.GetName().Version?.ToString(),
    Profiles = Somtum.Plara.PlaraProfiles.All
        .OrderBy(p => p.Assembly, StringComparer.Ordinal)
        .Select(p => new { p.Assembly, Source = p.Source.ToString().ToLowerInvariant(), p.Scale, p.GivesPp }),
});
app.MapPost("/pp", (PpRequest request, PlaraService plara, CancellationToken ct) => plara.PpAsync(request, ct));
app.MapPost("/stars", (StarsRequest request, PlaraService plara, CancellationToken ct) => plara.StarsAsync(request, ct));

app.Run();
