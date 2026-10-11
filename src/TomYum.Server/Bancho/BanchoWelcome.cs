using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Bancho;

/// <summary>
/// The notice osu! shows when a player logs in, from the website's admin panel ("In-game
/// welcome"): <c>bancho_welcome_enabled</c> ("0" turns it off) and <c>bancho_welcome_message</c>
/// (<c>{username}</c> is the player's name). Read again at most once a minute.
/// </summary>
public sealed class BanchoWelcome : IDisposable
{
    /// <summary>The welcome when staff haven't written their own.</summary>
    public const string DefaultMessage = "Welcome to TomYum, {username}!";

    public const string EnabledSetting = "bancho_welcome_enabled";
    public const string MessageSetting = "bancho_welcome_message";

    /// <summary>The longest welcome sent (osu!'s notification box isn't made for essays).</summary>
    public const int MaxLength = 500;

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory? _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<BanchoWelcome> _logger;
    private readonly SemaphoreSlim _load = new(1, 1);
    private (bool Enabled, string Message) _current = (true, DefaultMessage);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public BanchoWelcome(TimeProvider time, ILogger<BanchoWelcome> logger, IServiceScopeFactory? scopes = null)
    {
        _time = time;
        _logger = logger;
        _scopes = scopes;
    }

    /// <summary>The welcome for <paramref name="username"/>, or an empty string when it's turned off.</summary>
    public async Task<string> ForAsync(string username, CancellationToken cancellationToken = default)
    {
        var (enabled, message) = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return enabled ? Format(message, username) : string.Empty;
    }

    /// <summary><paramref name="message"/> with <c>{username}</c> filled in, cut to <see cref="MaxLength"/>.</summary>
    public static string Format(string message, string username)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.Replace("{username}", username, StringComparison.OrdinalIgnoreCase).Trim();
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    /// <summary>Reads the settings: unset is on with <see cref="DefaultMessage"/>; an empty message is the default too.</summary>
    public static (bool Enabled, string Message) FromSettings(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var enabled = !settings.TryGetValue(EnabledSetting, out var on) || on?.Trim() != "0";
        var message = settings.TryGetValue(MessageSetting, out var text) && !string.IsNullOrWhiteSpace(text) ? text : DefaultMessage;
        return (enabled, message);
    }

    private async Task<(bool Enabled, string Message)> CurrentAsync(CancellationToken cancellationToken)
    {
        if (_scopes is null || _time.GetUtcNow() - _loadedAt < CacheFor)
            return _current;

        await _load.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow() - _loadedAt < CacheFor)
                return _current;
            using var scope = _scopes.CreateScope();
            if (scope.ServiceProvider.GetService<OsuWebSiteSettings>() is { } settings)
                _current = FromSettings(await settings.GetAsync("bancho_welcome_", cancellationToken).ConfigureAwait(false));
        }
#pragma warning disable CA1031 // a login never fails over its welcome: the last one read is kept
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Couldn't read the in-game welcome from the website's settings.");
        }
        finally
        {
            _loadedAt = _time.GetUtcNow();
            _load.Release();
        }
        return _current;
    }

    public void Dispose() => _load.Dispose();
}
