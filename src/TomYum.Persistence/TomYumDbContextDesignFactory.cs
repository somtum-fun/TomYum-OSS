using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TomYum.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without a running server or database:
/// the MySQL version is pinned like the server pins it, and no connection is opened.
/// <code>dotnet ef migrations add &lt;Name&gt; --project src/TomYum.Persistence</code>
/// </summary>
public sealed class TomYumDbContextDesignFactory : IDesignTimeDbContextFactory<TomYumDbContext>
{
    public TomYumDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<TomYumDbContext>()
            .UseMySql("Server=localhost;Database=tomyum", new MySqlServerVersion(new Version(8, 4, 0)))
            .Options);
}
