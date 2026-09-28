using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Needs the internet: set <c>TOMYUM_TEST_NETWORK=1</c> to run.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOMYUM_TEST_NETWORK")))
            Skip = "Set TOMYUM_TEST_NETWORK=1 to run tests that download things.";
    }
}

public sealed class IpCountryTests
{
    [Theory]
    [InlineData("NL", "nl")]
    [InlineData(" th ", "th")]
    [InlineData("XX", null)] // Cloudflare: unknown
    [InlineData("T1", null)] // Cloudflare: Tor
    [InlineData("", null)]
    [InlineData("USA", null)]
    public void CloudflareHeaders_AreUsedWhenTheyNameACountry(string header, string? expected) =>
        Assert.Equal(expected, IpCountry.FromHeader(header));

    [Fact]
    public void WithoutADatabase_NothingIsKnown()
    {
        using var countries = New(Path.Combine(Path.GetTempPath(), "tomyum-geoip-" + Guid.NewGuid().ToString("N")), http: null);
        Assert.False(countries.IsLoaded);
        Assert.Null(countries.Lookup("8.8.8.8"));
        Assert.Equal("nl", countries.Resolve("NL", "8.8.8.8", "xx"));
        Assert.Equal("xx", countries.Resolve("XX", "8.8.8.8", "xx"));
    }

    [NetworkFact]
    public async Task DbIpsDatabase_IsDownloaded_AndPlacesPublicAddresses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tomyum-geoip-" + Guid.NewGuid().ToString("N"));
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        using (var countries = New(directory, http))
        {
            await countries.UpdateAsync();
            Assert.True(countries.IsLoaded);
            Assert.Equal("au", countries.Lookup("1.1.1.1"));
            Assert.Null(countries.Lookup("192.168.1.10")); // private: nobody's country
            Assert.Equal("au", countries.Resolve("XX", "1.1.1.1", "xx"));
        }

        // Next start: read from disk, no download.
        using (var again = New(directory, http: null))
            Assert.True(again.IsLoaded);
        Directory.Delete(directory, recursive: true);
    }

    private static IpCountry New(string directory, IHttpClientFactory? http) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GeoIp:Path"] = directory }).Build(),
            TimeProvider.System, NullLogger<IpCountry>.Instance, http);
}
