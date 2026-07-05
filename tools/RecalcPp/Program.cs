using System.Diagnostics;
using DotNetEnv;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TomYum.Performance;
using TomYum.Persistence;

namespace TomYum.Tools.RecalcPp;

/// <summary>
/// PP recalculation tool. Recomputes PP for all Best scores in the database and updates
/// weighted stats (PP + accuracy) for all affected users. Matches the recalc pattern from
/// osu-queue-score-statistics: batch processing, dry-run support, progress reporting.
/// </summary>
/// <remarks>
/// Usage:
///   dotnet run --project tools/RecalcPp -- [--dry-run] [--mode MODE] [--batch-size N]
/// Options:
///   --dry-run         Calculate PP but do not persist changes to the database
///   --mode MODE       Only recalc scores for the given mode (0=osu, 1=taiko, 2=catch, 3=mania)
///   --batch-size N    Number of scores to process per transaction (default 100)
/// </remarks>
internal static class Program
{
    private const int DefaultBatchSize = 100;

    private static async Task<int> Main(string[] args)
    {
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var mode = GetArgValue(args, "--mode");
        var batchSize = GetArgValue(args, "--batch-size") is { } bsStr && int.TryParse(bsStr, out var bs) ? bs : DefaultBatchSize;

        Console.WriteLine("TomYum PP Recalculation Tool");
        Console.WriteLine($"Mode: {(dryRun ? "DRY RUN (no changes will be saved)" : "LIVE")}");
        Console.WriteLine($"Filter: {(mode is not null ? $"mode {mode}" : "all modes")}");
        Console.WriteLine($"Batch size: {batchSize}");
        Console.WriteLine();

        var services = BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<TomYumDbContext>();
        var pp = sp.GetRequiredService<IPerformanceCalculator>();
        var logger = sp.GetRequiredService<ILogger<RecalculationEngine>>();

        var engine = new RecalculationEngine(db, pp, logger, dryRun, batchSize);
        var modeFilter = mode is not null && int.TryParse(mode, out var m) ? m : (int?)null;

        var sw = Stopwatch.StartNew();
        var result = await engine.RecalculateAsync(modeFilter, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Total scores processed: {result.ScoresProcessed}");
        Console.WriteLine($"PP recalculated: {result.ScoresRecalculated}");
        Console.WriteLine($"Users updated: {result.UsersUpdated}");
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");

        return 0;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        var idx = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static string ResolveConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")))
                return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "config");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var configDir = ResolveConfigDir();

        var dotEnvPath = Path.Combine(configDir, ".env");
        if (File.Exists(dotEnvPath))
            Env.Load(dotEnvPath);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddYamlFile(Path.Combine(configDir, "tomyum.yaml"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("TOMYUM_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        var dbOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration is missing");

        services.AddTomYumPersistence(dbOptions);
        services.AddSingleton<IMemoryCache, MemoryCache>();
        services.AddHttpClient<IPerformanceCalculator, OsuPerformanceCalculator>();

        return services.BuildServiceProvider();
    }
}
