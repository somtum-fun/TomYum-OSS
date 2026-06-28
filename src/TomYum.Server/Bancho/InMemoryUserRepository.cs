using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>
/// A throwaway in-memory <see cref="IUserRepository"/> for the login vertical slice, so the
/// handshake can be exercised end to end before <c>TomYum.Persistence</c> (EF Core + MySQL)
/// is implemented. Seeded with a couple of demo accounts.
/// </summary>
/// <remarks>
/// REMOVE when the real repository lands (next slice). Registered only so DI can satisfy
/// <see cref="IUserRepository"/>. Usernames are matched case-insensitively, mirroring the
/// repository contract.
/// </remarks>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _byUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, User> _byId = new();
    private int _nextId = 2;

    /// <summary>Seeds two demo users so a stable client can log in against the scaffold.</summary>
    public InMemoryUserRepository()
    {
        Seed(new User { Id = 1, Username = "TomYum" });
    }

    /// <inheritdoc />
    public Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var user) ? user : null);

    /// <inheritdoc />
    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byUsername.TryGetValue(username, out var user) ? user : null);

    /// <inheritdoc />
    public Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Honour a caller-supplied id (test seeding); otherwise assign the next free one.
        var id = user.Id != 0 ? user.Id : Interlocked.Increment(ref _nextId);
        var assigned = new User { Id = id, Username = user.Username, ApiKey = user.ApiKey };
        Seed(assigned);
        return Task.FromResult(assigned);
    }

    private void Seed(User user)
    {
        _byId[user.Id] = user;
        _byUsername[user.Username] = user;
    }
}
