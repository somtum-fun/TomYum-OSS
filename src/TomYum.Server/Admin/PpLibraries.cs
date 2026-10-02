using System.Text.Json;
using StackExchange.Redis;

namespace TomYum.Server.Admin;

/// <summary>
/// The pp libraries the server runs (ppy's for vanilla, akatsuki-pp-rs for Relax/Autopilot), as the
/// host's pp updater (<c>deploy/pp-updater</c>, outside Docker) reports them through Redis: what's
/// running, what's newest, and the update it's running or last ran. Asking it to check or to update
/// goes the same way; once an update is live it asks for the pp rework (<see cref="PpReworks"/>).
/// </summary>
public sealed class PpLibraries
{
    public const string StatusKey = "tomyum:pp-libraries";
    public const string JobKey = "tomyum:pp-library-update";
    public const string RequestKey = "tomyum:pp-library-requests";
    public const string ReworkKey = "tomyum:pp-rework-requests";

    private readonly IConnectionMultiplexer? _redis;

    /// <param name="redis">Where the updater listens (none: no updater).</param>
    public PpLibraries(IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
    }

    /// <summary>The updater's report and its update job (each null when there's none yet).</summary>
    public async Task<object> StatusAsync()
    {
        if (_redis is null)
            return new { Status = (JsonElement?)null, Job = (JsonElement?)null };
        var db = _redis.GetDatabase();
        return new
        {
            Status = Parse(await db.StringGetAsync(StatusKey).ConfigureAwait(false)),
            Job = Parse(await db.StringGetAsync(JobKey).ConfigureAwait(false)),
        };
    }

    /// <summary>Asks the updater to look for newer libraries now.</summary>
    public async Task<bool> CheckAsync()
    {
        if (_redis is null)
            return false;
        await _redis.GetDatabase().ListLeftPushAsync(RequestKey, JsonSerializer.Serialize(new { action = "check" })).ConfigureAwait(false);
        return true;
    }

    /// <summary>Asks the updater to update <paramref name="library"/>; why not, when it can't.</summary>
    public async Task<string?> UpdateAsync(string library, int actorId)
    {
        if (_redis is null)
            return "The pp updater isn't set up on this server.";
        if (library is not ("vanilla" or "akatsuki"))
            return "Unknown library.";

        var db = _redis.GetDatabase();
        if (Parse(await db.StringGetAsync(JobKey).ConfigureAwait(false)) is { } job
            && job.TryGetProperty("state", out var state) && state.GetString() == "running")
            return "An update is running already.";
        if (await db.ListLengthAsync(RequestKey).ConfigureAwait(false) > 0)
            return "The updater has a request waiting already.";

        await db.ListLeftPushAsync(RequestKey, JsonSerializer.Serialize(new { action = "update", library, actorId })).ConfigureAwait(false);
        return null;
    }

    /// <summary>The next pp rework the updater asked for (an update went live), if any.</summary>
    public async Task<PpReworkRequestFromUpdater?> TakeReworkRequestAsync()
    {
        if (_redis is null)
            return null;
        var raw = await _redis.GetDatabase().ListRightPopAsync(ReworkKey).ConfigureAwait(false);
        return raw.IsNullOrEmpty ? null : JsonSerializer.Deserialize<PpReworkRequestFromUpdater>(raw.ToString(), JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement? Parse(RedisValue value)
    {
        if (value.IsNullOrEmpty)
            return null;
        try
        {
            using var document = JsonDocument.Parse(value.ToString());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A pp rework the updater asks for once an update is live.</summary>
public sealed record PpReworkRequestFromUpdater(string Library, IReadOnlyList<int> Modes, string? FromVersion, string? ToVersion, string? Reason, int ActorId);

/// <summary>Which pp library to update: <c>vanilla</c> or <c>akatsuki</c>.</summary>
public sealed record PpLibraryUpdateRequest(string Library);
