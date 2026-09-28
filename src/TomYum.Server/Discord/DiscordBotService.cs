using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

namespace TomYum.Server.Discord;

/// <summary>
/// The bot's connection to Discord (Discord.Net): logs in with <see cref="DiscordOptions.Token"/>,
/// registers <see cref="DiscordCommands.Specs"/> in the configured server, answers slash commands,
/// feeds the relayed channel to <see cref="DiscordChatRelay"/>, and posts/sets roles for the rest
/// of the bot (<see cref="IDiscordGateway"/>). Needs the "Message Content" intent enabled in the
/// developer portal for the chat relay.
/// </summary>
public sealed partial class DiscordBotService : BackgroundService, IDiscordGateway
{
    /// <summary>How long a command may run before the bot tells Discord it's still thinking (Discord allows 3 s).</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> PrivateCommands = new(StringComparer.Ordinal) { "link", "unlink", "staff" };

    private readonly DiscordOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly DiscordSocketClient _client;
    private volatile bool _ready;

    public DiscordBotService(DiscordOptions options, IServiceProvider services, ILogger<DiscordBotService> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info,
        });
        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += _ =>
        {
            _ready = false;
            return Task.CompletedTask;
        };
        _client.SlashCommandExecuted += command =>
        {
            // Discord.Net's gateway thread must not wait on the database.
            _ = Task.Run(() => AnswerAsync(command));
            return Task.CompletedTask;
        };
        _client.MessageReceived += message =>
        {
            if (message.Channel.Id == _options.ChatChannelId && _options.ChatChannelId != 0)
                _ = Task.Run(() => RelayAsync(message));
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _ready && _client.ConnectionState == ConnectionState.Connected;

    public async Task PostAsync(ulong channelId, DiscordPost post, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (channelId == 0 || !IsConnected)
            return;
        try
        {
            if (_client.GetGuild(_options.GuildId)?.GetTextChannel(channelId) is not { } channel)
            {
                _logger.LogWarning("Discord: channel {ChannelId} isn't in the server (or the bot can't see it).", channelId);
                return;
            }
            await channel.SendMessageAsync(
                text: Cut(post.Content, 2000),
                embed: post.Embed is null ? null : Build(post.Embed),
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // a failed post is logged and dropped; it must not stop the caller
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Discord: couldn't post to channel {ChannelId}.", channelId);
        }
    }

    public async Task<bool> SyncRolesAsync(ulong discordUserId, IReadOnlySet<ulong> managed, IReadOnlySet<ulong> wanted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(wanted);
        if (!IsConnected)
            return false;

        var options = new RequestOptions { CancelToken = cancellationToken };
        var member = await _client.Rest.GetGuildUserAsync(_options.GuildId, discordUserId, options).ConfigureAwait(false);
        if (member is null)
            return false;

        var has = member.RoleIds.ToHashSet();
        var add = wanted.Where(r => managed.Contains(r) && !has.Contains(r)).ToList();
        var remove = managed.Where(r => has.Contains(r) && !wanted.Contains(r)).ToList();
        try
        {
            if (add.Count > 0)
                await member.AddRolesAsync(add, options).ConfigureAwait(false);
            if (remove.Count > 0)
                await member.RemoveRolesAsync(remove, options).ConfigureAwait(false);
        }
        catch (global::Discord.Net.HttpException ex)
        {
            _logger.LogWarning(ex, "Discord: couldn't change {Member}'s roles (the bot's role must be above the roles it gives).", discordUserId);
            return false;
        }
        return true;
    }

    /// <summary>Discord's custom emoji (<c>&lt;:name:id&gt;</c>) as <c>:name:</c>, for the game chat.</summary>
    public static string PlainEmoji(string text) => CustomEmoji().Replace(text ?? string.Empty, ":$1:");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _client.LoginAsync(TokenType.Bot, _options.Token).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);
        }
        catch (global::Discord.Net.HttpException ex)
        {
            _logger.LogError(ex, "Discord: couldn't log in; check Discord:Token.");
            return;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        _ready = false;
        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    private async Task OnReadyAsync()
    {
        if (_client.GetGuild(_options.GuildId) is not { } guild)
        {
            _logger.LogError("Discord: the bot isn't in server {GuildId}; invite it first.", _options.GuildId);
            return;
        }

        try
        {
            var commands = DiscordCommands.Specs.Select(ToSlashCommand).ToArray();
            await guild.BulkOverwriteApplicationCommandAsync(commands).ConfigureAwait(false);
            _ready = true;
            _logger.LogInformation("Discord: connected to {Guild} as {Bot}; {Count} commands registered.", guild.Name, _client.CurrentUser, commands.Length);
        }
        catch (global::Discord.Net.HttpException ex)
        {
            _ready = true;
            _logger.LogError(ex, "Discord: couldn't register the slash commands (the bot needs the applications.commands scope).");
        }
    }

    private static ApplicationCommandProperties ToSlashCommand(DiscordCommandSpec spec)
    {
        var builder = new SlashCommandBuilder().WithName(spec.Name).WithDescription(spec.Description);
        foreach (var option in spec.Options)
        {
            var optionBuilder = new SlashCommandOptionBuilder()
                .WithName(option.Name)
                .WithDescription(option.Description)
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(option.Required);
            foreach (var choice in option.Choices ?? [])
                optionBuilder.AddChoice(choice, choice);
            builder.AddOption(optionBuilder);
        }
        return builder.Build();
    }

    private async Task AnswerAsync(SocketSlashCommand command)
    {
        var options = command.Data.Options.ToDictionary(
            o => o.Name,
            o => Convert.ToString(o.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            StringComparer.Ordinal);
        var commands = _services.GetRequiredService<DiscordCommands>();
        var run = commands.RunAsync(command.Data.Name, options, command.User.Id);

        try
        {
            if (await Task.WhenAny(run, Task.Delay(AnswerWithin)).ConfigureAwait(false) == run)
            {
                var reply = await run.ConfigureAwait(false);
                await command.RespondAsync(Cut(reply.Post.Content, 2000), embed: Build(reply.Post.Embed), ephemeral: reply.Private, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            else
            {
                await command.DeferAsync(ephemeral: PrivateCommands.Contains(command.Data.Name)).ConfigureAwait(false);
                var reply = await run.ConfigureAwait(false);
                await command.FollowupAsync(Cut(reply.Post.Content, 2000), embed: Build(reply.Post.Embed), ephemeral: reply.Private, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // any failure becomes a reply, never an unanswered command
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Discord: /{Command} failed.", command.Data.Name);
            try
            {
                if (command.HasResponded)
                    await command.FollowupAsync("Something went wrong; try again in a bit.", ephemeral: true).ConfigureAwait(false);
                else
                    await command.RespondAsync("Something went wrong; try again in a bit.", ephemeral: true).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // the interaction may have expired: nothing more to do
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private async Task RelayAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage || message.Author.IsBot || message.Author.IsWebhook || message.Source != MessageSource.User)
            return;

        var text = PlainEmoji(message.CleanContent);
        if (message.Attachments.Count > 0)
            text = string.Join(' ', message.Attachments.Select(a => a.Url).Prepend(text));
        try
        {
            var outcome = await _services.GetRequiredService<DiscordChatRelay>().FromDiscordAsync(message.Author.Id, text).ConfigureAwait(false);
            var reaction = outcome switch
            {
                RelayOutcome.NotLinked => "🔗",
                RelayOutcome.Muted => "🔇",
                _ => null,
            };
            if (reaction is not null)
                await message.AddReactionAsync(new Emoji(reaction)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a message that couldn't be relayed is logged; the bot carries on
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Discord: couldn't relay a message to the game.");
        }
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical or LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        // Gateway reconnects are routine; Discord.Net reports them as errors with an exception.
        if (message.Exception is GatewayReconnectException or TaskCanceledException)
            level = LogLevel.Information;
#pragma warning disable CA1848, CA2254 // Discord.Net's own messages, forwarded as they come
        _logger.Log(level, message.Exception, "Discord.Net {Source}: {Message}", message.Source, message.Message);
#pragma warning restore CA1848, CA2254
        return Task.CompletedTask;
    }

    private static Embed? Build(DiscordEmbed? embed)
    {
        if (embed is null)
            return null;
        var builder = new EmbedBuilder
        {
            Title = Cut(embed.Title, 256),
            Url = embed.Url,
            Description = Cut(embed.Description, 4096),
            ThumbnailUrl = embed.ThumbnailUrl,
            ImageUrl = embed.ImageUrl,
        };
        if (embed.Color is { } color)
            builder.WithColor(new Color(color));
        if (embed.AuthorName is { Length: > 0 } author)
            builder.WithAuthor(Cut(author, 256), embed.AuthorIconUrl, embed.AuthorUrl);
        if (embed.Footer is { Length: > 0 } footer)
            builder.WithFooter(Cut(footer, 2048));
        if (embed.Timestamp is { } timestamp)
            builder.WithTimestamp(timestamp);
        foreach (var field in embed.Fields.Take(25))
            builder.AddField(Cut(field.Name, 256), Cut(field.Value, 1024), field.Inline);
        return builder.Build();
    }

    private static string? Cut(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"<a?:(\w{2,32}):\d{5,20}>")]
    private static partial Regex CustomEmoji();
}
