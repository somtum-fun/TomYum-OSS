using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Repositories;
using TomYum.Persistence.Repositories;

namespace TomYum.Persistence;

/// <summary>
/// DI wiring for the EF Core + MySQL persistence layer. Keeps the registration details (provider,
/// connection string composition, repository bindings) inside this library so the composition root
/// (<c>TomYum.Server</c>) only calls <see cref="AddTomYumPersistence"/>.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TomYumDbContext"/> against Pomelo MySQL using the supplied
    /// <paramref name="options"/>, and wires the Domain repository ports to their EF Core
    /// implementations. The repositories are scoped (matching the DbContext lifetime). The caller
    /// binds and validates <see cref="DatabaseOptions"/> from configuration.
    /// </summary>
    public static IServiceCollection AddTomYumPersistence(this IServiceCollection services, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        var connectionString = options.BuildConnectionString();

        // Pin the server version explicitly rather than ServerVersion.AutoDetect, which opens a
        // blocking probe connection at startup (fails the whole host if the DB is briefly down).
        // Matches the MySQL 8.4 container; override via DatabaseOptions if the deployment differs.
        var serverVersion = new MySqlServerVersion(new Version(8, 4, 0));
        services.AddDbContext<TomYumDbContext>(db => db.UseMySql(connectionString, serverVersion));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBeatmapRepository, BeatmapRepository>();

        return services;
    }
}
