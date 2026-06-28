using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/> over <see cref="TomYumDbContext"/>.
/// All by-name lookups go through the canonical <see cref="User.SafeName"/> (see
/// <see cref="User.MakeSafeName"/>), matching the in-memory repository's contract.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly TomYumDbContext _db;

    /// <summary>Creates the repository over the database context.</summary>
    public UserRepository(TomYumDbContext db) => _db = db;

    /// <inheritdoc />
    public Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        GetBySafeNameAsync(User.MakeSafeName(username), cancellationToken);

    /// <inheritdoc />
    public Task<User?> GetBySafeNameAsync(string safeName, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.SafeName == safeName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> SafeNameExistsAsync(string safeName, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().AnyAsync(u => u.SafeName == safeName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().AnyAsync(u => u.Email == email, cancellationToken);

    /// <inheritdoc />
    public async Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The id is database-generated; the tracked entity now carries it. Detach so callers get a
        // clean instance and the next operation does not collide on a tracked graph.
        _db.Entry(user).State = EntityState.Detached;
        return user;
    }

    /// <inheritdoc />
    public async Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        _db.Users.Update(user);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(user).State = EntityState.Detached;
        return user;
    }
}
