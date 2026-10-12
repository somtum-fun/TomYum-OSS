using TomYum.Plugins.Abstractions;
using TomYum.Server.Plugins;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Plugins are trusted by the id they declare, so no two may claim the same one.</summary>
public sealed class PluginIdTests
{
    private static PluginManifest Manifest(string id) => new(id, id, "1.0.0", "someone", string.Empty);

    [Fact]
    public void IdsClaimedTwice_AreFound_WhateverTheirCase()
    {
        var shared = PluginHostService.SharedIds([Manifest("tomyum.votes"), Manifest("tomyum.dan"), Manifest("TomYum.Votes"), Manifest("tomyum-metrics")]);

        Assert.Equal(["tomyum.votes"], shared.Select(id => id.ToLowerInvariant()));
        Assert.Contains("TOMYUM.VOTES", shared);
    }

    [Fact]
    public void DifferentIds_AreAllFine()
    {
        Assert.Empty(PluginHostService.SharedIds([Manifest("tomyum.votes"), Manifest("tomyum.dan")]));
    }
}
