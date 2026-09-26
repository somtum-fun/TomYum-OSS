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
        var dryRun    = args.Contains("--dry-run",   StringComparer.OrdinalIgnoreCase);
        var force     = args.Contains("--force",     StringComparer.OrdinalIgnoreCase);
        var backfill  = args.Contains("--backfill",  StringComparer.OrdinalIgnoreCase);
        var allPassed = args.Contains("--all-passed", StringComparer.OrdinalIgnoreCase);
        var mode      = GetArgValue(args, "--mode");
        var batchSize = GetArgValue(args, "--batch-size") is { } bsStr && int.TryParse(bsStr, out var bs) ? bs : DefaultBatchSize;

        Console.WriteLine("TomYum PP Recalculation Tool");
        Console.WriteLine($"Mode: {(dryRun ? "DRY RUN (no changes will be saved)" : "LIVE")}");
        if (force)    Console.WriteLine("Force: enabled (PP written for every score regardless of change)");
        if (backfill) Console.WriteLine("Backfill: enabled (will resolve missing beatmap rows before recalc)");
        Console.WriteLine($"Filter: {(mode is not null ? $"mode {mode}" : "all modes")}");
        Console.WriteLine($"Batch size: {batchSize}");
        Console.WriteLine();

        var services = BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db         = sp.GetRequiredService<TomYumDbContext>();
        var pp         = sp.GetRequiredService<IPerformanceCalculator>();
        var logFactory = sp.GetRequiredService<ILoggerFactory>();

        var sw = Stopwatch.StartNew();

        if (backfill)
        {
            Console.WriteLine("--- Backfilling beatmap rows ---");
            var configuration = sp.GetRequiredService<IConfiguration>();
            var apiKey        = configuration["Osu:ApiKey"];
            var http          = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var bfLog         = logFactory.CreateLogger<BeatmapBackfiller>();
            var backfiller    = new BeatmapBackfiller(db, http, bfLog, apiKey, dryRun);
            await backfiller.BackfillAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine();
        }

        Console.WriteLine("--- Recalculating PP ---");
        var engine     = new RecalculationEngine(db, pp, logFactory.CreateLogger<RecalculationEngine>(), dryRun, batchSize, force, allPassed);
        var modeFilter = mode is not null && int.TryParse(mode, out var m) ? m : (int?)null;

        var result = await engine.RecalculateAsync(modeFilter, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Total scores processed: {result.ScoresProcessed}");
        Console.WriteLine($"PP recalculated:        {result.ScoresRecalculated}");
        Console.WriteLine($"Users updated:          {result.UsersUpdated}");
        Console.WriteLine($"Elapsed:                {sw.Elapsed.TotalSeconds:F1}s");

        if (result.ScoreChanges.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== Score Changes ===");
            Console.WriteLine($"{"Score ID",-10} {"Mode",-8} {"Beatmap",-10} {"Old PP",10} {"New PP",10} {"Delta",10}");
            Console.WriteLine(new string('-', 62));
            foreach (var sc in result.ScoreChanges.OrderByDescending(s => Math.Abs(s.NewPp - s.OldPp)))
            {
                var delta = sc.NewPp - sc.OldPp;
                var sign  = delta >= 0 ? "+" : "";
                Console.WriteLine($"{sc.ScoreId,-10} {ModeLabel(sc.Mode),-8} {sc.BeatmapId,-10} {sc.OldPp,10:F2} {sc.NewPp,10:F2} {sign}{delta,9:F2}");
            }
        }

        if (result.UserStatChanges.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== Player PP Changes ===");
            Console.WriteLine($"{"User ID",-10} {"Mode",-8} {"Old PP",10} {"New PP",10} {"PP Δ",10} {"Old Acc",10} {"New Acc",10} {"Acc Δ",10}");
            Console.WriteLine(new string('-', 82));
            foreach (var uc in result.UserStatChanges.OrderByDescending(u => Math.Abs(u.NewPp - u.OldPp)))
            {
                var ppDelta  = uc.NewPp - uc.OldPp;
                var accDelta = uc.NewAcc - uc.OldAcc;
                var ppSign   = ppDelta  >= 0 ? "+" : "";
                var accSign  = accDelta >= 0 ? "+" : "";
                Console.WriteLine(
                    $"{uc.UserId,-10} {ModeLabel(uc.Mode),-8} {uc.OldPp,10:F2} {uc.NewPp,10:F2} {ppSign}{ppDelta,9:F2} " +
                    $"{uc.OldAcc * 100,9:F2}% {uc.NewAcc * 100,9:F2}% {accSign}{accDelta * 100,8:F2}%");
            }
        }

        return 0;
    }

    private static string ModeLabel(int mode) => mode switch
    {
        0 => "osu!",
        1 => "taiko",
        2 => "catch",
        3 => "mania",
        _ => $"mode{mode}",
    };

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
        services.AddSingleton<IConfiguration>(configuration);

        var dbOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration is missing");

        services.AddTomYumPersistence(dbOptions);
        // Pluggable pp engine (routing calculator + backends), same wiring as the server so
        // relax/autopilot scores recalc through their configured backend. See ADR-0010.
        services.AddTomYumPerformance(configuration);
        services.AddHttpClient(); // registers IHttpClientFactory for BeatmapBackfiller

        return services.BuildServiceProvider();
    }
}
