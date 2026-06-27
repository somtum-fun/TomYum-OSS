using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>
/// Persistence port for <see cref="User"/> aggregates. Implemented in
/// <c>TomYum.Persistence</c>; the domain owns only this contract (Dependency
/// Inversion — see ADR-0001).
/// </summary>
public interface IUserRepository
{
    /// <summary>Loads a user by id, or <c>null</c> when none exists.</summary>
    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Loads a user by exact username (case-insensitive), or <c>null</c>.</summary>
    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new user and returns it with its assigned id.</summary>
    Task<User> AddAsync(User user, CancellationToken cancellationToken = default);
}
