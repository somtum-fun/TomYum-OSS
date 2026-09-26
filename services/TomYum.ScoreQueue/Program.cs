using TomYum.Caching;
using TomYum.Persistence;
using TomYum.ScoreQueue;

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

var builder = Host.CreateApplicationBuilder(args);

var configDir = ResolveConfigDir(AppContext.BaseDirectory);

var dotEnvPath = Path.Combine(configDir, ".env");
if (File.Exists(dotEnvPath))
    DotNetEnv.Env.Load(dotEnvPath);

builder.Configuration
    .AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddYamlFile(Path.Combine(configDir, "tomyum.yaml"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

builder.Services.AddTomYumCaching(builder.Configuration);

var dbOptions = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
if (dbOptions.Enabled)
    builder.Services.AddTomYumPersistence(dbOptions);

builder.Services.AddHostedService<ScoreStatisticsConsumer>();

var host = builder.Build();
host.Run();
