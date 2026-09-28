using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Discord;

namespace TomYum.Server.Tests.Discord;

/// <summary>BanchoBot's command set over the given services (and optionally the Discord link codes).</summary>
internal static class BanchoBotTestFactory
{
    public static BanchoBotCommands Commands(IServiceProvider services, DiscordLinkCodes? discordCodes) => new(
        new TomYumVersion(),
        new PlayerRegistry(),
        new ChannelRegistry(),
        new MatchRegistry(),
        new BeatmapService(new NoMaps()),
        services.GetRequiredService<IServiceScopeFactory>(),
        new Plugins.PluginCommandRegistry(),
        new Plugins.LoadedPluginRegistry(),
        discordCodes: discordCodes);

    private sealed class NoMaps : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
