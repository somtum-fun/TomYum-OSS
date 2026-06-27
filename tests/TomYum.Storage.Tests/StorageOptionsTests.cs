using TomYum.Storage;
using Xunit;

namespace TomYum.Storage.Tests;

/// <summary>
/// Scaffold-phase sanity test. Confirms the storage options model exists and its
/// defaults match ADR-0006 (enabled-by-default, empty routes/providers). Replace with
/// real router/provider tests when implementations land.
/// </summary>
public class StorageOptionsTests
{
    [Fact]
    public void Defaults_AreEnabledWithNoRoutes()
    {
        var options = new StorageOptions();

        Assert.True(options.Enabled);
        Assert.Empty(options.Providers);
        Assert.Empty(options.Routes);
        Assert.Empty(options.DefaultProviders);
        Assert.Equal("storage", StorageOptions.SectionName);
    }

    [Fact]
    public void AssetRoute_SupportsMultiTargetFanOut()
    {
        // An asset type can be hosted on one, several, or all providers (WriteTo),
        // while reads are served from a single chosen provider (ReadFrom).
        var route = new AssetRoute { ReadFrom = "r2" };
        route.WriteTo.Add("r2");
        route.WriteTo.Add("ovh-s3");
        route.WriteTo.Add("local");

        Assert.True(route.Enabled);
        Assert.Equal(3, route.WriteTo.Count);
        Assert.Equal("r2", route.ReadFrom);
    }
}
