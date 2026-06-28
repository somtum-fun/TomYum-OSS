using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>
/// A throwaway in-memory <see cref="IUserRepository"/> for the login + registration
/// vertical slice, so the handshake and account creation can be exercised end to end
/// before <c>TomYum.Persistence</c> (EF Core + MySQL) is implemented.
/// </summary>
/// <remarks>
/// REMOVE when the real repository lands (next slice). All by-name lookups go through the
/// canonical <see cref="User.SafeName"/>, mirroring the persistence contract.
/// </remarks>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _bySafeName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, User> _byId = new();
    private int _nextId = 2;

    /// <inheritdoc />
    public Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var user) ? user : null);

    /// <inheritdoc />
    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        GetBySafeNameAsync(User.MakeSafeName(username), cancellationToken);

    /// <inheritdoc />
    public Task<User?> GetBySafeNameAsync(string safeName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bySafeName.TryGetValue(safeName, out var user) ? user : null);

    /// <inheritdoc />
    public Task<bool> SafeNameExistsAsync(string safeName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bySafeName.ContainsKey(safeName));

    /// <inheritdoc />
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bySafeName.Values.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Honour a caller-supplied id (test seeding); otherwise assign the next free one.
        var id = user.Id != 0 ? user.Id : Interlocked.Increment(ref _nextId);
        var safeName = string.IsNullOrEmpty(user.SafeName) ? User.MakeSafeName(user.Username) : user.SafeName;
        var assigned = new User
        {
            Id = id,
            Username = user.Username,
            SafeName = safeName,
            Email = user.Email,
            PasswordBcrypt = user.PasswordBcrypt,
            Country = user.Country,
            CreationTime = user.CreationTime,
            LatestActivity = user.LatestActivity,
            ApiKey = user.ApiKey,
            Privileges = user.Privileges,
        };

        _byId[assigned.Id] = assigned;
        _bySafeName[assigned.SafeName] = assigned;
        return Task.FromResult(assigned);
    }

    /// <inheritdoc />
    public Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!_byId.ContainsKey(user.Id))
        {
            throw new InvalidOperationException($"Cannot update user {user.Id}: no such user.");
        }

        // Overwrite both indexes with the supplied copy (safe name is immutable here).
        var safeName = string.IsNullOrEmpty(user.SafeName) ? User.MakeSafeName(user.Username) : user.SafeName;
        _byId[user.Id] = user;
        _bySafeName[safeName] = user;
        return Task.FromResult(user);
    }
}
