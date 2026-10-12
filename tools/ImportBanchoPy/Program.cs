using System.Globalization;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TomYum.Persistence;

namespace TomYum.Tools.ImportBanchoPy;

/// <summary>
/// Imports a bancho.py server (bancho.py, bancho.py-ex, kawata.py and forks of them) into TomYum +
/// osu-web: players with their ids, passwords and groups, scores with their ids, replays, maps and
/// staff statuses, stats, medals, friends, devices, the staff log, and (where the fork has them)
/// Discord links, rank history and nomination history. See README.md.
/// </summary>
/// <remarks>
/// Usage (from the repository root, with TomYum's TOMYUM_* settings in the environment):
///   dotnet run --project tools/ImportBanchoPy -- --source bpy_import --data /import/data          # plan only
///   dotnet run --project tools/ImportBanchoPy -- --source bpy_import --data /import/data --apply  # import (server stopped)
///   dotnet run --project tools/ImportBanchoPy -- --source bpy_import --data /import/data --custom-sets --rework --server-url http://server:8080  # then, server running
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? source = null, data = null, flavor = null, serverUrl = null, domain = null, outDir = null;
        var apply = false;
        var customSets = false;
        var rework = false;
        var dan = false;
        var allowExisting = false;
        var skipEmptyRestricted = false;
        var oldBotId = 1;
        var steps = new List<string>();
        var remaps = new Dictionary<int, int>();

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--source": source = Next(); break;
                case "--data": data = Next(); break;
                case "--out": outDir = Next(); break;
                case "--flavor": flavor = Next(); break;
                case "--apply": apply = true; break;
                case "--custom-sets": customSets = true; break;
                case "--rework": rework = true; break;
                case "--dan": dan = true; break;
                case "--allow-existing": allowExisting = true; break;
                case "--skip-empty-restricted": skipEmptyRestricted = true; break;
                case "--server-url": serverUrl = Next(); break;
                case "--domain": domain = Next(); break;
                case "--old-bot-id": oldBotId = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--step": steps.Add(Next()); break;
                case "--remap":
                    {
                        var parts = Next().Split('=');
                        if (parts.Length != 2)
                            throw new ArgumentException("--remap takes OLD=NEW.");
                        remaps[int.Parse(parts[0], CultureInfo.InvariantCulture)] = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    }
                case "-h" or "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    PrintUsage();
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("--source is required: the name of bancho.py's database on this MySQL server (the restored dump, e.g. bpy_import).");
            return 2;
        }
        foreach (var step in steps.Where(s => !Importer.Steps.Contains(s, StringComparer.Ordinal)))
        {
            Console.Error.WriteLine($"Unknown step '{step}'. Steps: {string.Join(", ", Importer.Steps)}.");
            return 2;
        }

        var configuration = BuildConfiguration();
        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration is missing.");
        if (!options.UseOsuWebAccounts)
        {
            Console.Error.WriteLine("Set Database:Enabled=true and Database:OsuWebName (TOMYUM_DATABASE__OSUWEBNAME=osu): the import needs the website.");
            return 2;
        }

        var settings = new ImportSettings
        {
            Source = source,
            TomYum = options.Name,
            OsuWeb = options.OsuWebName!,
            DataPath = data is null ? null : Path.GetFullPath(data),
            OutDir = Path.GetFullPath(outDir ?? "import-out"),
            ReplaysPath = Path.GetFullPath(configuration["Replays:Path"] ?? "data/replays"),
            OsuCachePath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuration["Performance:OsuCacheDirectory"]) ? "data/osu-cache" : configuration["Performance:OsuCacheDirectory"]!),
            BotUserId = configuration.GetValue<int?>("Bancho:BotUserId") ?? 3,
            OldBotId = oldBotId,
            Remaps = remaps,
            SkipEmptyRestricted = skipEmptyRestricted,
            AllowExisting = allowExisting,
            ScoreProcessor = configuration.GetValue<bool?>("OsuWeb:ScoreProcessor") ?? false,
            ServerUrl = serverUrl is null ? null : new Uri(serverUrl, UriKind.Absolute),
            Domain = domain ?? configuration["Server:Domain"],
            InteropSecret = configuration["OsuWeb:InteropSecret"],
            RedisConfiguration = configuration["Redis:Configuration"] ?? "localhost:6379",
            IndexPrefix = options.OsuWebIndexPrefix,
        };
        Directory.CreateDirectory(settings.OutDir);
        await using var log = new StreamWriter(Path.Combine(settings.OutDir, $"import-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log")) { AutoFlush = true };

        Console.WriteLine("bancho.py -> TomYum + osu-web import");
        Console.WriteLine($"Source: {options.Host}/{settings.Source}    TomYum: {settings.TomYum}    osu-web: {settings.OsuWeb}");
        Console.WriteLine($"BanchoBot id {Db.N(settings.BotUserId)} (Bancho:BotUserId); the old server's bot is {Db.N(settings.OldBotId)}; replays go to {settings.ReplaysPath}" +
                          (settings.DataPath is null ? "" : $"; data from {settings.DataPath}"));
        Console.WriteLine(customSets || rework ? "Mode: AFTER THE IMPORT (the server must be running)" : apply ? "Mode: APPLY (TomYum's server must be stopped)" : "Mode: PLAN (nothing is changed; add --apply to run it)");
        Console.WriteLine();

        await using var db = new Db(options.BuildConnectionString());
        await db.OpenAsync();
        var schema = await SourceSchema.DetectAsync(db, source, flavor is null ? null : SourceSchema.ParseFlavor(flavor));
        Console.WriteLine($"Source flavour: {schema.Flavor}" + (flavor is null ? " (detected from its tables; --flavor overrides)" : ""));

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConfiguration(configuration.GetSection("Logging")).AddConsole())
            .AddTomYumPersistence(options)
            .BuildServiceProvider();
        await using var _ = services.ConfigureAwait(false);

        var importer = new Importer(db, schema, settings, services, log);
        await importer.EnsureSourceIndexesAsync();

        if (customSets || rework || dan)
        {
            await importer.EnsureTablesAsync();
            var custom = new CustomSets(db, schema, settings, importer, services, log);
            if (customSets)
            {
                await custom.EnsureTableAsync();
                Console.WriteLine("Private sets:");
                var summary = await custom.RunAsync();
                Console.WriteLine(summary);
                log.WriteLine($"{Importer.Now()} custom_sets: {summary}");
            }
            if (dan)
            {
                var summary = await custom.ImportDanAsync();
                Console.WriteLine(summary);
                log.WriteLine($"{Importer.Now()} dan: {summary}");
            }
            if (rework)
            {
                var summary = await custom.StartReworkAsync();
                Console.WriteLine(summary);
                log.WriteLine($"{Importer.Now()} rework: {summary}");
            }
            return 0;
        }

        // --- the plan -------------------------------------------------------------------------
        Console.WriteLine("Plan:");
        var plan = await importer.PlanAsync();
        var blocked = false;
        foreach (var line in plan)
        {
            var text = $"  {line.Label,-46} {line.Count,9:N0}" + (line.Note is null ? "" : $"   {line.Note}");
            Console.WriteLine(text);
            log.WriteLine(text);
            blocked |= line.Note is not null && (line.Note.StartsWith("old id", StringComparison.Ordinal) || line.Note.StartsWith("lazer", StringComparison.Ordinal)
                                                 || line.Note.Contains("rename one", StringComparison.Ordinal) || line.Note.Contains("fails on the second", StringComparison.Ordinal));
        }
        Console.WriteLine();
        if (blocked)
        {
            Console.WriteLine("BLOCKED: fix the lines marked above (ids, names or emails), then run again.");
            return 1;
        }
        if (!apply)
        {
            Console.WriteLine("Plan only. Stop TomYum's server, back up the databases, then run again with --apply.");
            return 0;
        }

        // --- apply ----------------------------------------------------------------------------
        if (await importer.CheckTargetAsync() is { } problem)
        {
            Console.WriteLine($"BLOCKED: {problem}");
            return 1;
        }
        await importer.EnsureTablesAsync();
        var finished = await importer.FinishedStepsAsync();
        if (finished.Count == 0 || steps.Count == 0)
            await importer.BuildUserMapAsync();

        var toRun = steps.Count > 0 ? steps : Importer.Steps.Where(s => !finished.Contains(s)).ToList();
        if (toRun.Count == 0)
        {
            Console.WriteLine("Every step has run already (tomyum.bpy_import_steps). Name one with --step to run it again.");
            return 0;
        }
        foreach (var step in toRun)
        {
            var started = DateTime.UtcNow;
            Console.Write($"{step}: ");
            var summary = await importer.RunStepAsync(step);
            var took = (DateTime.UtcNow - started).TotalSeconds;
            Console.WriteLine($"{summary} ({took.ToString("F1", CultureInfo.InvariantCulture)} s)");
            log.WriteLine($"{Importer.Now()} {step}: {summary} ({took.ToString("F1", CultureInfo.InvariantCulture)} s)");
        }

        Console.WriteLine();
        Console.WriteLine("Done. Next: start the server, then run again with --custom-sets (if the plan listed private sets) and --rework; then the media command on the website, and check a few profiles (README.md).");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            dotnet run --project tools/ImportBanchoPy -- --source <db> [--data <.data>] [--apply | --custom-sets] [options]

              --source <db>          bancho.py's database on this MySQL server (the restored dump), e.g. bpy_import. Required.
              --data <path>          bancho.py's .data folder: osr/ (replays) and osz/ (private sets).
              --apply                Write. Without it the plan is printed and nothing changes.
              --custom-sets          Upload the old server's own sets through the running server and re-point their scores.
              --rework               Start a pp rework of every mode on the running server (always do this after an import).
              --dan                  osu!dan: the old server's dan maps (maps.is_dan) as courses of the tomyum.dan plugin, with clears from the scores.
              --server-url <url>     Where the server answers for --custom-sets, e.g. http://server:8080.
              --domain <domain>      The server's domain (default Server:Domain); uploads go to osu.<domain>.
              --step <name>          Run only this step (repeatable): users groups moderation connections friends beatmaps
                                     scores replays stats achievements rank_history favourites hashes staff_log nominations mirror.
              --flavor <f>           banchopy, banchopy-ex, kawata or somtum (default: detected from the tables).
              --old-bot-id <id>      The old server's bot (default 1): written as BanchoBot, gets no account.
              --remap OLD=NEW        Give a player another id (one whose old id is BanchoBot's here).
              --skip-empty-restricted  Leave out restricted accounts that never played, logged in or sent a device.
              --allow-existing       Go ahead although the target already has players or scores.
              --out <dir>            Where the log and csv files go (default ./import-out).

            Settings come from TomYum's configuration (TOMYUM_DATABASE__*, TOMYUM_REDIS__CONFIGURATION, TOMYUM_REPLAYS__PATH,
            TOMYUM_BANCHO__BOTUSERID, TOMYUM_OSUWEB__SCOREPROCESSOR, TOMYUM_OSUWEB__INTEROPSECRET, TOMYUM_SERVER__DOMAIN).
            """);
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
