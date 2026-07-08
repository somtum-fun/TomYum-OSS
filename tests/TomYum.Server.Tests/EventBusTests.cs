using Microsoft.Extensions.Logging.Abstractions;

using TomYum.Plugins.Abstractions;
using TomYum.Server.Plugins;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>The plugin event bus dispatches to subscribers, honours veto, and unsubscribes on dispose.</summary>
public class EventBusTests
{
    private sealed record Ping(int Value) : IPluginEvent;

    private sealed class Gate : VetoableEvent;

    [Fact]
    public async Task Subscribe_then_Publish_invokes_the_handler()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var seen = 0;
        bus.Subscribe<Ping>((e, _) => { seen = e.Value; return Task.CompletedTask; });

        await bus.PublishAsync(new Ping(42));

        Assert.Equal(42, seen);
    }

    [Fact]
    public async Task Dispose_of_the_subscription_stops_delivery()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var count = 0;
        var subscription = bus.Subscribe<Ping>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(new Ping(1));
        subscription.Dispose();
        await bus.PublishAsync(new Ping(1));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Veto_stops_dispatch_to_later_handlers()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var laterRan = false;
        bus.Subscribe<Gate>((e, _) => { e.Veto("nope"); return Task.CompletedTask; });
        bus.Subscribe<Gate>((_, _) => { laterRan = true; return Task.CompletedTask; });

        var gate = new Gate();
        await bus.PublishAsync(gate);

        Assert.True(gate.IsVetoed);
        Assert.Equal("nope", gate.VetoReason);
        Assert.False(laterRan);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_dispatch_to_others()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var reached = false;
        bus.Subscribe<Ping>((_, _) => throw new InvalidOperationException("boom"));
        bus.Subscribe<Ping>((_, _) => { reached = true; return Task.CompletedTask; });

        await bus.PublishAsync(new Ping(1));

        Assert.True(reached);
    }
}
