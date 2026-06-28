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

    /// <summary>
    /// Loads a user by canonical <see cref="User.SafeName"/> (see <see cref="User.MakeSafeName"/>),
    /// or <c>null</c>. This is the authoritative by-name lookup used at login.
    /// </summary>
    Task<User?> GetBySafeNameAsync(string safeName, CancellationToken cancellationToken = default);

    /// <summary>Whether a user already exists with the given canonical safe name.</summary>
    Task<bool> SafeNameExistsAsync(string safeName, CancellationToken cancellationToken = default);

    /// <summary>Whether a user already exists with the given email (case-insensitive).</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new user and returns it with its assigned id.</summary>
    Task<User> AddAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes to an existing user (matched by <see cref="User.Id"/>), returning the
    /// stored copy. Used for mutations such as privilege grants. Throws when no user with that id
    /// exists.
    /// </summary>
    Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default);
}
