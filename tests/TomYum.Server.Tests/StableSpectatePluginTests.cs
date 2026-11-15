using System.Buffers.Binary;
using System.Collections;
using System.Reflection;

using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Loader;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Guards the loose-source <c>tomyum.stable-spectate</c> plugin (plugins/tomyum-stable-spectate): it must
/// compile under the loader's Roslyn, stay off (saying why) without what it needs, and read stable spectator
/// frame bundles, today's layout and old builds' alike, for osu!lazer viewers.
/// </summary>
public class StableSpectatePluginTests
{
    [Fact]
    public async Task Loose_source_compiles_and_stays_off_without_its_services()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        var logger = new Logger();
        var events = new Events();
        try
        {
            Assert.Equal("tomyum.stable-spectate", plugin.Manifest.Id);
            await plugin.InitializeAsync(new Context(logger, events));
            var warning = Assert.Single(logger.Warnings);
            Assert.Contains("Redis", warning, StringComparison.Ordinal);
            Assert.Contains("Plugins:Trusted", warning, StringComparison.Ordinal);
            Assert.Equal(0, events.Subscriptions);
            await plugin.ShutdownAsync();
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)] // today's layout: score frame with the ScoreV2 flag, then the sequence number
    [InlineData(false)] // an old build's: the 28-byte score frame and nothing after it
    public async Task Reads_frames_action_and_score(bool todaysLayout)
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        try
        {
            var bundle = Read(plugin, Bundle(todaysLayout, action: 0, (1000, 256f, 192f, 5), (1016, 260.5f, 190f, 0)));
            Assert.NotNull(bundle);
            var frames = ((IEnumerable)Get(bundle!, "Frames")).Cast<object>().ToList();
            Assert.Equal(2, frames.Count);
            Assert.Equal(1000, Get(frames[0], "Time"));
            Assert.Equal(256f, Get(frames[0], "X"));
            Assert.Equal((byte)5, Get(frames[0], "Buttons"));
            Assert.Equal(260.5f, Get(frames[1], "X"));
            Assert.Equal((byte)0, Get(bundle!, "Action"));
            var score = Get(bundle!, "Score");
            Assert.Equal(123, Get(score, "Count300"));
            Assert.Equal(4, Get(score, "CountMiss"));
            Assert.Equal(987654L, Get(score, "TotalScore"));
            Assert.Equal(321, Get(score, "MaxCombo"));
            Assert.Equal(17, Get(score, "Combo"));
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Refuses_what_isnt_a_bundle()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        try
        {
            Assert.Null(Read(plugin, Array.Empty<byte>()));
            // Claims 50 frames, carries one.
            var truncated = Bundle(true, 0, (1, 1f, 1f, 0));
            BinaryPrimitives.WriteUInt16LittleEndian(truncated.AsSpan(4), 50);
            Assert.Null(Read(plugin, truncated));
            // An action osu! doesn't have.
            Assert.Null(Read(plugin, Bundle(true, action: 42)));
            // A completion with no frames is a bundle.
            Assert.Equal((byte)3, Get(Read(plugin, Bundle(true, action: 3))!, "Action"));
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Bundle(bool todaysLayout, byte action, params (int Time, float X, float Y, byte Buttons)[] frames)
    {
        var bytes = new List<byte>();
        void I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); bytes.AddRange(b); }
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); bytes.AddRange(b); }
        void F32(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); bytes.AddRange(b); }

        I32(0);
        U16(frames.Length);
        foreach (var f in frames)
        {
            bytes.Add(f.Buttons);
            bytes.Add(0);
            F32(f.X);
            F32(f.Y);
            I32(f.Time);
        }
        bytes.Add(action);
        // Score frame: time, id, 300/100/50/geki/katu/miss, score, max combo, combo, perfect, hp, tag.
        I32(1016);
        bytes.Add(0);
        U16(123); U16(5); U16(2); U16(30); U16(9); U16(4);
        I32(987654);
        U16(321); U16(17);
        bytes.Add(0); bytes.Add(200); bytes.Add(0);
        if (todaysLayout)
        {
            bytes.Add(0); // no ScoreV2
            U16(7); // sequence
        }
        return bytes.ToArray();
    }

    private static object? Read(IPlugin plugin, byte[] payload)
    {
        var parser = plugin.GetType().Assembly.GetType("TomYum.Plugins.StableSpectate.StableFrames", throwOnError: true)!;
        var read = parser.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!;
        // ReadOnlySpan can't be boxed: go through a small generic-free wrapper.
        var wrapper = typeof(StableSpectatePluginTests).GetMethod(nameof(Invoke), BindingFlags.NonPublic | BindingFlags.Static)!;
        return wrapper.Invoke(null, new object[] { read, payload });
    }

    private static object? Invoke(MethodInfo read, byte[] payload)
    {
        var del = read.CreateDelegate<ReadSpan>();
        return del(payload);
    }

    private delegate object? ReadSpan(ReadOnlySpan<byte> payload);

    private static object Get(object target, string property) =>
        target.GetType().GetProperty(property)!.GetValue(target)!;

    private static async Task<(string Root, IPlugin Plugin, PluginLoader Loader)> LoadRealPluginAsync()
    {
        var source = FindRealPluginDir();
        var root = Path.Combine(Path.GetTempPath(), "tomyum-stable-spectate-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "tomyum-stable-spectate");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        var loader = new PluginLoader(allowLooseSource: true);
        var plugins = await loader.LoadAllAsync(root);
        var plugin = Assert.Single(plugins);
        return (root, plugin, loader);
    }

    private static string FindRealPluginDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "tomyum-stable-spectate");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate plugins/tomyum-stable-spectate from the test base directory.");
    }

    private sealed class Logger : IPluginLogger
    {
        public List<string> Warnings { get; } = new();

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message, Exception? exception = null) => Warnings.Add(message);
    }

    private sealed class Events : IEventBus
    {
        public int Subscriptions { get; private set; }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IPluginEvent
        {
            Subscriptions++;
            return new Unsub(() => Subscriptions--);
        }

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IPluginEvent =>
            Task.CompletedTask;

        private sealed class Unsub(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class Context(IPluginLogger logger, IEventBus events) : IPluginContext
    {
        public IPluginConfig Config { get; } = new EmptyConfig();

        public IPluginLogger Logger { get; } = logger;

        public IEventBus Events { get; } = events;

        public ICommandRegistry Commands => throw new NotSupportedException();

        public IHttpRouteRegistry Http => throw new NotSupportedException();

        public IPluginDataAccess? Data => null;

        public TService? GetHostService<TService>() where TService : class => null;
    }

    private sealed class EmptyConfig : IPluginConfig
    {
        public T? Get<T>(string key, T? defaultValue = default) => defaultValue;

        public TOptions Bind<TOptions>() where TOptions : new() => new();
    }
}
