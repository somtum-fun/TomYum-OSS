using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TomYum.Caching;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Tools.MigrateToOsuWeb;

/// <summary>
/// One-time move of an existing TomYum server into osu-web: players become osu-web accounts,
/// TomYum's tables are renumbered to the osu-web user ids, and the history (maps, scores, stats)
/// is copied to osu-web. Run it BEFORE switching the server to osu-web accounts. See README.md.
/// </summary>
/// <remarks>
/// Usage (from the repository root):
///   dotnet run --project tools/MigrateToOsuWeb                    # dry run: print the plan only
///   dotnet run --project tools/MigrateToOsuWeb -- --apply         # do it
///   dotnet run --project tools/MigrateToOsuWeb -- --apply --drop-unmapped
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var dropUnmapped = args.Contains("--drop-unmapped", StringComparer.OrdinalIgnoreCase);

        var configuration = BuildConfiguration();
        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration is missing.");
        if (!options.UseOsuWebAccounts)
        {
            Console.Error.WriteLine("Set Database:Enabled=true and Database:OsuWebName (e.g. TOMYUM_DATABASE__OSUWEBNAME=osu).");
            return 2;
        }

        Console.WriteLine("TomYum -> osu-web migration");
        Console.WriteLine($"TomYum database: {options.Host}/{options.Name}    osu-web database: {options.Host}/{options.OsuWebName}");
        Console.WriteLine(apply ? "Mode: APPLY" : "Mode: DRY RUN (nothing is changed; add --apply to run it)");
        Console.WriteLine();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConfiguration(configuration.GetSection("Logging")).AddConsole())
            .AddTomYumPersistence(options)
            .AddScoped<OsuWebAccountMigration>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // --- 1. accounts ---------------------------------------------------------------------
        var migration = sp.GetRequiredService<OsuWebAccountMigration>();
        var plan = await migration.PlanAsync();
        if (plan.AlreadyDone)
        {
            Console.WriteLine("Accounts: already moved to osu-web earlier; continuing with the history copy.");
        }
        else
        {
            PrintPlan(plan);
            if (plan.Blockers.Count > 0 || plan.Conflicts.Any() || (!dropUnmapped && plan.Unmapped.Any()))
            {
                Console.WriteLine();
                foreach (var blocker in plan.Blockers)
                    Console.WriteLine($"BLOCKED: {blocker}");
                if (plan.Conflicts.Any())
                    Console.WriteLine("BLOCKED: resolve the conflicts above (rename the account on one side), then run again.");
                if (!dropUnmapped && plan.Unmapped.Any())
                    Console.WriteLine("BLOCKED: some ids have data but no account; add --drop-unmapped to delete that data.");
                return 1;
            }

            if (!apply)
            {
                Console.WriteLine();
                Console.WriteLine("Dry run: nothing changed. Back up both databases, then run again with --apply.");
                return 0;
            }

            var result = await migration.ApplyAsync(plan, dropUnmapped);
            Console.WriteLine();
            Console.WriteLine($"Accounts: {result.Created} created, {result.Linked} linked, {result.Dropped} dropped; " +
                              $"{result.ScoresRemapped} scores renumbered, {result.ScoresDeleted} deleted.");
        }

        if (!apply)
        {
            Console.WriteLine("Dry run: the history copy runs with --apply.");
            return 0;
        }

        // --- 2. history ----------------------------------------------------------------------
        var db = sp.GetRequiredService<TomYumDbContext>();
        var beatmaps = await sp.GetRequiredService<OsuWebBeatmapMirror>()
            .BackfillAsync(await db.Beatmaps.AsNoTracking().ToListAsync());
        Console.WriteLine($"Beatmaps: {beatmaps} copied.");

        var progress = new Progress<int>(n => Console.Write($"\rScores: {n} copied..."));
        var copied = await sp.GetRequiredService<OsuWebScoreMirror>().CopyHistoryAsync(progress);
        Console.WriteLine($"\rScores: {copied.Count} copied; stats and global ranks rebuilt.");

        // --- 3. search index -----------------------------------------------------------------
        await QueueForIndexAsync(configuration, options, copied);

        Console.WriteLine();
        Console.WriteLine("Done. Now switch the server to osu-web accounts (TOMYUM_DATABASE__OSUWEBNAME) and restart it.");
        return 0;
    }

    private static async Task QueueForIndexAsync(IConfiguration configuration, DatabaseOptions options, IReadOnlyList<ulong> scoreIds)
    {
        if (scoreIds.Count == 0)
            return;

        var redisConfig = configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Configuration)}"] ?? "localhost:6379";
        try
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConfig);
            var indexing = new OsuWebScoreIndexing(redis, options.OsuWebIndexPrefix);
            foreach (var batch in scoreIds.Chunk(1000))
                await indexing.QueueAsync(batch);
            Console.WriteLine($"Search index: {scoreIds.Count} scores queued for osu-web's score-indexer.");
        }
        catch (RedisConnectionException ex)
        {
            Console.WriteLine($"Search index: could not reach Redis at {redisConfig} ({ex.Message}).");
            Console.WriteLine("  The scores are in osu-web's database but not in its search index yet; see README.md.");
        }
    }

    private static void PrintPlan(AccountMigrationPlan plan)
    {
        Console.WriteLine($"{"TomYum id",-10} {"Username",-20} {"Action",-9} {"osu-web id",-10} Reason");
        Console.WriteLine(new string('-', 90));
        foreach (var e in plan.Entries)
        {
            var name = e.Username.Length == 0 ? "(no account)" : e.Username;
            Console.WriteLine($"{e.OldId,-10} {name,-20} {e.Action,-9} {(e.NewId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "new"),-10} {e.Reason}");
        }
        Console.WriteLine();
        Console.WriteLine($"{plan.Entries.Count(e => e.Action == AccountMigrationAction.Create)} to create, " +
                          $"{plan.Entries.Count(e => e.Action == AccountMigrationAction.Link)} to link, " +
                          $"{plan.Entries.Count(e => e.Action == AccountMigrationAction.Drop)} to drop, " +
                          $"{plan.Conflicts.Count()} conflicts.");
    }

    private static IConfiguration BuildConfiguration()
    {
        var configDir = ResolveConfigDir();
        var dotEnvPath = Path.Combine(configDir, ".env");
        if (File.Exists(dotEnvPath))
            Env.Load(dotEnvPath);

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddYamlFile(Path.Combine(configDir, "tomyum.yaml"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("TOMYUM_")
            .Build();
    }

    private static string ResolveConfigDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")))
                return Path.Combine(dir.FullName, "config");
        }
        return Path.Combine(AppContext.BaseDirectory, "config");
    }
}
