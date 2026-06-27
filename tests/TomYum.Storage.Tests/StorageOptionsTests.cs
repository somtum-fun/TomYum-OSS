using TomYum.Storage;
using Xunit;

namespace TomYum.Storage.Tests;

/// <summary>
/// Scaffold-phase sanity test. Confirms the storage options model exists and its
/// defaults match ADR-0006 (enabled-by-default, empty routes). Replace with real
/// router/provider tests when implementations land.
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
        Assert.Equal("storage", StorageOptions.SectionName);
    }
}
