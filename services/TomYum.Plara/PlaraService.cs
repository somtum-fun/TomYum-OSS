using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.IO;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Skinning;
using Somtum.Plara;

namespace TomYum.Plara;

/// <param name="RulesetDirectory">Where pinned ruleset DLLs are kept, by SHA-256.</param>
/// <param name="OsuDirectory">The .osu cache TomYum's server shares (custom maps' .osu files are only there).</param>
public sealed record PlaraOptions(string RulesetDirectory, string OsuDirectory);

/// <summary>A custom ruleset's pinned release: the DLL TumThai downloads, which scores must have been played on.</summary>
public record RulesetRef(string Assembly, string DownloadUrl, string Sha256);

/// <summary>A map in a ruleset, with mods (lazer's JSON: [{acronym, settings}]).</summary>
public sealed record StarsRequest(string Assembly, string DownloadUrl, string Sha256, int BeatmapId, JsonElement? Mods)
    : RulesetRef(Assembly, DownloadUrl, Sha256);

/// <summary>A play: the score's own numbers, as osu-web stores them (statistics and maximum_statistics by result name).</summary>
public sealed record PpRequest(
    string Assembly, string DownloadUrl, string Sha256, int BeatmapId, JsonElement? Mods,
    Dictionary<string, int>? Statistics, Dictionary<string, int>? MaximumStatistics, double Accuracy, bool Passed)
    : RulesetRef(Assembly, DownloadUrl, Sha256);

/// <param name="Pp">Null when the play gives none (failed, a mod without pp, judgements that don't fit the map, an error).</param>
/// <param name="Stars">The map's stars with the play's mods (null when it couldn't be rated).</param>
/// <param name="Version">Pla-ra's version (PlaraProfiles.VERSION) the numbers are from.</param>
/// <param name="Reason">Why there's no pp, when there isn't.</param>
/// <param name="Retry">True when it failed for a reason that may pass (a download, a timeout): ask again later.</param>
public sealed record PpResponse(double? Pp, double? Stars, int Version, string? Reason = null, bool Retry = false);

public sealed record StarsResponse(double? Stars, int Version, string? Reason = null);

/// <summary>
/// Loads pinned ruleset DLLs (checked against their SHA-256) into the default load context (osu! looks rulesets up
/// with Type.GetType, which a load context of their own breaks), and rates maps and plays with Pla-ra.
/// </summary>
public sealed class PlaraService : IDisposable
{
    public const string HttpClientName = "plara";
    private const long MaxDllBytes = 50 * 1024 * 1024;
    private static readonly TimeSpan CalculationTimeout = TimeSpan.FromSeconds(30);

    private readonly PlaraOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PlaraService> _log;
    private readonly ConcurrentDictionary<string, Ruleset> _rulesets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _loadedAssemblies = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loading = new(1, 1);

    public PlaraService(PlaraOptions options, IHttpClientFactory http, IMemoryCache cache, IHostApplicationLifetime lifetime, ILogger<PlaraService> log)
    {
        _options = options;
        _http = http;
        _cache = cache;
        _lifetime = lifetime;
        _log = log;
        Directory.CreateDirectory(options.RulesetDirectory);
        Directory.CreateDirectory(options.OsuDirectory);
    }

    public void Dispose() => _loading.Dispose();

    public async Task<StarsResponse> StarsAsync(StarsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var ruleset = await RulesetAsync(request, cancellationToken).ConfigureAwait(false);
            var apiMods = ParseMods(request.Mods);
            var rated = await RateAsync(ruleset, request.Sha256, request.BeatmapId, apiMods, cancellationToken).ConfigureAwait(false);
            return new StarsResponse(rated.Stars, PlaraProfiles.VERSION);
        }
        catch (PlaraException ex)
        {
            return new StarsResponse(null, PlaraProfiles.VERSION, ex.Message);
        }
    }

    public async Task<PpResponse> PpAsync(PpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var ruleset = await RulesetAsync(request, cancellationToken).ConfigureAwait(false);
            var profile = PlaraCalculators.ProfileFor(ruleset)
                ?? throw new PlaraException($"Pla-ra has no profile for {ruleset.GetType().Assembly.GetName().Name}.");
            var apiMods = ParseMods(request.Mods);
            var rated = await RateAsync(ruleset, request.Sha256, request.BeatmapId, apiMods, cancellationToken).ConfigureAwait(false);

            if (!request.Passed)
                return new PpResponse(null, rated.Stars, PlaraProfiles.VERSION, "The play didn't pass.");

            var statistics = Judgements(request.Statistics);
            var maximum = Judgements(request.MaximumStatistics);
            // The play's judgements must add up to the map's notes, as the ruleset converts it with the play's mods:
            // made-up statistics (a scripted submit) don't get pp.
            int claimed = PlaraPerformanceCalculator.BasicCount(maximum);
            int judged = PlaraPerformanceCalculator.BasicCount(statistics);
            if (claimed != rated.Objects || judged > claimed)
                return new PpResponse(null, rated.Stars, PlaraProfiles.VERSION, $"The play's judgements ({judged} of {claimed}) don't fit the map ({rated.Objects} notes).");

            double? pp = PlaraPerformanceCalculator.Calculate(profile, rated.Stars, rated.Objects, request.Accuracy,
                statistics.GetValueOrDefault(HitResult.Miss), apiMods.Select(m => m.Acronym));

            return pp is { } value
                ? new PpResponse(Math.Round(value, 3), rated.Stars, PlaraProfiles.VERSION)
                : new PpResponse(null, rated.Stars, PlaraProfiles.VERSION, profile.GivesPp ? "A mod of the play gives no pp." : "This ruleset gives no pp.");
        }
        catch (PlaraException ex)
        {
            return new PpResponse(null, null, PlaraProfiles.VERSION, ex.Message, ex.Retry);
        }
    }

    /// <summary>The map's stars and judged notes in the ruleset with the mods (kept a day: a pinned DLL never changes).</summary>
    private async Task<(double Stars, int Objects)> RateAsync(Ruleset ruleset, string sha256, int beatmapId, List<APIMod> apiMods, CancellationToken cancellationToken)
    {
        string key = $"stars:{sha256}:{beatmapId}:{string.Join(',', apiMods.Select(m => JsonConvert.SerializeObject(m)))}";
        if (_cache.TryGetValue(key, out (double, int) known))
            return known;

        byte[] osu = await OsuFileAsync(beatmapId, cancellationToken).ConfigureAwait(false);
        Mod[] mods = apiMods.Select(m => m.ToMod(ruleset)).ToArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CalculationTimeout);
        var token = timeout.Token;
        try
        {
            var rated = await Task.Run(() =>
            {
                var working = new BytesWorkingBeatmap(osu);
                var attributes = PlaraCalculators.CreateDifficultyCalculator(ruleset, working).Calculate(mods, token);
                int objects = attributes is PlaraDifficultyAttributes plara
                    ? plara.ObjectCount
                    : PlaraDifficultyCalculator.JudgedObjects(working.GetPlayableBeatmap(ruleset.RulesetInfo, mods, token).HitObjects).Count;
                double stars = double.IsFinite(attributes.StarRating) ? Math.Round(attributes.StarRating, 4) : 0;
                return (stars, objects);
            }, token).ConfigureAwait(false);

            _cache.Set(key, rated, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(1), Size = 1 });
            return rated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaraException($"Rating beatmap {beatmapId} took longer than {CalculationTimeout.TotalSeconds:0} s.", retry: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PlaraException)
        {
            _log.LogWarning(ex, "Pla-ra: couldn't rate beatmap {BeatmapId} in {Ruleset}.", beatmapId, ruleset.ShortName);
            throw new PlaraException($"The ruleset couldn't rate beatmap {beatmapId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The pinned ruleset: downloaded once, checked against its SHA-256, kept by it.</summary>
    private async Task<Ruleset> RulesetAsync(RulesetRef pin, CancellationToken cancellationToken)
    {
        if (_rulesets.TryGetValue(pin.Sha256, out var ruleset))
            return ruleset;
        if (!Uri.TryCreate(pin.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || pin.Sha256.Length != 64)
            throw new PlaraException("A ruleset needs an https download_url and a sha256.");

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rulesets.TryGetValue(pin.Sha256, out ruleset))
                return ruleset;

            string path = Path.Combine(_options.RulesetDirectory, pin.Sha256.ToLowerInvariant() + ".dll");
            if (!File.Exists(path) || !HashMatches(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), pin.Sha256))
            {
                byte[] bytes;
                try
                {
                    bytes = await _http.CreateClient(HttpClientName).GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new PlaraException($"Couldn't download the ruleset: {ex.Message}", retry: true);
                }
                if (bytes.Length > MaxDllBytes || !HashMatches(bytes, pin.Sha256))
                    throw new PlaraException("The downloaded ruleset isn't the pinned one (its SHA-256 differs).");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }

            // One release of an assembly per process: the default load context can't hold two. A new pin of a ruleset
            // already loaded restarts the sidecar (Docker starts it again), which then loads the new one.
            string name = AssemblyName.GetAssemblyName(path).Name ?? pin.Assembly;
            if (_loadedAssemblies.TryGetValue(name, out string? loadedSha) && !loadedSha.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Pla-ra: {Assembly} was pinned again; restarting to load the new release.", name);
                _lifetime.StopApplication();
                throw new PlaraException($"{name} was pinned again: the sidecar is restarting to load it.", retry: true);
            }

            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            var type = types.FirstOrDefault(t => t.IsSubclassOf(typeof(Ruleset)) && !t.IsAbstract)
                ?? throw new PlaraException($"{name} has no ruleset in it.");
            ruleset = (Ruleset)Activator.CreateInstance(type)!;

            _loadedAssemblies[name] = pin.Sha256;
            _rulesets[pin.Sha256] = ruleset;
            _log.LogInformation("Pla-ra: loaded {Assembly} ({Sha}).", name, pin.Sha256[..12]);
            return ruleset;
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>The map's .osu: from the cache TomYum's server shares, or osu.ppy.sh (then cached).</summary>
    private async Task<byte[]> OsuFileAsync(int beatmapId, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_options.OsuDirectory, beatmapId.ToString(CultureInfo.InvariantCulture) + ".osu");
        if (File.Exists(path))
        {
            byte[] cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached.Length > 0)
                return cached;
        }

        byte[] bytes;
        try
        {
            bytes = await _http.CreateClient(HttpClientName)
                .GetByteArrayAsync(new Uri($"https://osu.ppy.sh/osu/{beatmapId.ToString(CultureInfo.InvariantCulture)}"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PlaraException($"Couldn't get beatmap {beatmapId}'s .osu: {ex.Message}", retry: true);
        }
        if (bytes.Length == 0)
            throw new PlaraException($"Beatmap {beatmapId} has no .osu.");

        string temporary = path + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return bytes;
    }

    private static bool HashMatches(byte[] bytes, string sha256) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase);

    private static List<APIMod> ParseMods(JsonElement? mods) =>
        mods is { ValueKind: JsonValueKind.Array } array
            ? JsonConvert.DeserializeObject<List<APIMod>>(array.GetRawText()) ?? []
            : [];

    private static Dictionary<HitResult, int> Judgements(Dictionary<string, int>? statistics)
    {
        var result = new Dictionary<HitResult, int>();
        foreach (var (name, count) in statistics ?? [])
        {
            if (Enum.TryParse<HitResult>(name.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var hit))
                result[hit] = result.GetValueOrDefault(hit) + count;
        }
        return result;
    }

    /// <summary>A beatmap from .osu bytes: only its objects are used.</summary>
    private sealed class BytesWorkingBeatmap : WorkingBeatmap
    {
        private readonly Beatmap _beatmap;

        public BytesWorkingBeatmap(byte[] file)
            : this(Decode(file))
        {
        }

        private BytesWorkingBeatmap(Beatmap beatmap)
            : base(beatmap.BeatmapInfo, null)
        {
            _beatmap = beatmap;
        }

        private static Beatmap Decode(byte[] file)
        {
            using var reader = new LineBufferedReader(new MemoryStream(file, writable: false));
            return osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        protected override IBeatmap GetBeatmap() => _beatmap;
        public override Texture GetBackground() => throw new NotSupportedException();
        protected override Track GetBeatmapTrack() => throw new NotSupportedException();
        protected override ISkin GetSkin() => throw new NotSupportedException();
        public override Stream GetStream(string storagePath) => throw new NotSupportedException();
    }
}

/// <summary>Why a ruleset, map or play couldn't be rated (the answer says so instead of failing).</summary>
public sealed class PlaraException : Exception
{
    public PlaraException(string message, bool retry = false)
        : base(message)
    {
        Retry = retry;
    }

    public PlaraException()
    {
    }

    public PlaraException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public bool Retry { get; }
}
