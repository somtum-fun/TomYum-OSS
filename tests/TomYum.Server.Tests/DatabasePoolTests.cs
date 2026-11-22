using MySqlConnector;
using TomYum.Persistence;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>TomYum's connection pools stay well under MySQL's connection limit unless the settings say otherwise.</summary>
public class DatabasePoolTests
{
    [Fact]
    public void ComposedString_GetsThePoolLimits()
    {
        var options = new DatabaseOptions { Host = "db", Name = "tomyum", User = "u", Password = "p" };
        var built = new MySqlConnectionStringBuilder(options.BuildConnectionString());
        Assert.Equal(48u, built.MaximumPoolSize);
        Assert.Equal(60u, built.ConnectionIdleTimeout);
        Assert.Equal("db", built.Server);

        var osuWeb = new MySqlConnectionStringBuilder(new DatabaseOptions { Host = "db", OsuWebName = "osu" }.BuildOsuWebConnectionString());
        Assert.Equal(48u, osuWeb.MaximumPoolSize);
        Assert.Equal("osu", osuWeb.Database);
    }

    [Theory]
    [InlineData("Server=db;Database=x;Maximum Pool Size=5")]
    [InlineData("Server=db;Database=x;MaxPoolSize=5")]
    public void AnExplicitPoolSize_Wins(string connectionString)
    {
        var built = new MySqlConnectionStringBuilder(new DatabaseOptions { ConnectionString = connectionString, MaxPoolSize = 50 }.BuildConnectionString());
        Assert.Equal(5u, built.MaximumPoolSize);
    }

    [Fact]
    public void Zero_LeavesMySqlConnectorsDefault()
    {
        var built = new MySqlConnectionStringBuilder(new DatabaseOptions { Host = "db", MaxPoolSize = 0, PoolIdleSeconds = 0 }.BuildConnectionString());
        Assert.Equal(100u, built.MaximumPoolSize);
        Assert.Equal(180u, built.ConnectionIdleTimeout);
    }
}
