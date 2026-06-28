using System.Text.RegularExpressions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;

namespace TomYum.Server.Bancho;

/// <summary>
/// Validates and (optionally) creates accounts for the osu!stable in-game registration flow
/// (<c>POST /users</c> on the <c>osu.</c> host). Mirrors bancho.py's rules: the client sends
/// the form repeatedly with <c>check != 0</c> to validate each field as the user types, then
/// once with <c>check == 0</c> to actually create the account. See
/// <c>memories/protocols/bancho-stable.md</c> §3 / the registration study note.
/// </summary>
public sealed partial class RegistrationService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<RegistrationService> _logger;

    /// <summary>Creates the service over the user repository and password hasher.</summary>
    public RegistrationService(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        ILogger<RegistrationService> logger)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    // Username: 2-15 chars of word chars, space, square brackets, or hyphen. (bancho.py regexes.py)
    [GeneratedRegex(@"^[\w \[\]-]{2,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    // Email: local@domain with one or more dotted labels. (bancho.py regexes.py)
    [GeneratedRegex(@"^[^@\s]{1,200}@[^@\s\.]{1,30}(?:\.[^@\.\s]{2,24})+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    /// <summary>
    /// The result of a registration attempt. <see cref="Errors"/> is empty on success;
    /// when non-empty, each entry maps a form field key (<c>username</c>, <c>user_email</c>,
    /// <c>password</c>) to its newline-joined error message. <see cref="Created"/> is the new
    /// user when validation passed and creation was requested (<c>check == 0</c>).
    /// </summary>
    public sealed record RegistrationResult(IReadOnlyDictionary<string, string> Errors, User? Created)
    {
        /// <summary>Whether the attempt passed all validation.</summary>
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Validates the supplied fields and, when <paramref name="create"/> is <see langword="true"/>
    /// and validation passes, creates the account. <paramref name="create"/> corresponds to the
    /// client's <c>check == 0</c> final submit; <see langword="false"/> is a validate-only pass.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(
        string username,
        string email,
        string plaintextPassword,
        bool create,
        string country,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, List<string>>();

        await ValidateUsernameAsync(username, errors, cancellationToken).ConfigureAwait(false);
        await ValidateEmailAsync(email, errors, cancellationToken).ConfigureAwait(false);
        ValidatePassword(plaintextPassword, errors);

        if (errors.Count > 0)
        {
            // Join each field's messages with '\n' into a single string, mirroring bancho.py.
            var joined = errors.ToDictionary(kvp => kvp.Key, kvp => string.Join('\n', kvp.Value));
            return new RegistrationResult(joined, null);
        }

        if (!create)
        {
            // Validate-only pass (check != 0): all good, but don't create the account yet.
            return new RegistrationResult(EmptyErrors, null);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bcrypt = _passwordHasher.HashPassword(_passwordHasher.ComputeMd5Hex(plaintextPassword));
        var user = new User
        {
            Username = username,
            SafeName = User.MakeSafeName(username),
            Email = email,
            PasswordBcrypt = bcrypt,
            Country = country,
            CreationTime = now,
            LatestActivity = now,
        };

        var created = await _users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        // The first real registration (id 3; 1 = BanchoBot, 2 = the dev-seed user) is the server
        // owner and is granted Owner privileges automatically, mirroring bancho.py granting the
        // first account staff. The id is only known after AddAsync, so re-add with the elevated
        // privileges (the repository overwrites by id/safe-name).
        if (created.Id == OwnerUserId && created.Privileges != UserPrivileges.Owner)
        {
            created = await _users.AddAsync(CloneWith(created, UserPrivileges.Owner), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Granted Owner privileges to first registration {Username} (id {UserId}).", created.Username, created.Id);
        }

        _logger.LogInformation("Registered new player {Username} (id {UserId}).", created.Username, created.Id);
        return new RegistrationResult(EmptyErrors, created);
    }

    /// <summary>
    /// The user id of the server owner: the first real registration. Ids 1 and 2 are reserved for
    /// BanchoBot and the development seed user respectively, so the first player to register is id 3.
    /// </summary>
    public const int OwnerUserId = 3;

    /// <summary>Returns a copy of <paramref name="user"/> with its <see cref="User.Privileges"/> replaced.</summary>
    private static User CloneWith(User user, UserPrivileges privileges) => new()
    {
        Id = user.Id,
        Username = user.Username,
        SafeName = user.SafeName,
        Email = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country = user.Country,
        CreationTime = user.CreationTime,
        LatestActivity = user.LatestActivity,
        ApiKey = user.ApiKey,
        Privileges = privileges,
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyErrors =
        new Dictionary<string, string>();

    private async Task ValidateUsernameAsync(
        string username,
        Dictionary<string, List<string>> errors,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();

        if (!UsernameRegex().IsMatch(username))
        {
            messages.Add("Must be 2-15 characters in length.");
        }

        if (username.Contains('_', StringComparison.Ordinal) && username.Contains(' ', StringComparison.Ordinal))
        {
            messages.Add("May contain \"_\" and \" \", but not both.");
        }

        // Uniqueness is only meaningful when the name is otherwise well-formed.
        if (messages.Count == 0 &&
            await _users.SafeNameExistsAsync(User.MakeSafeName(username), cancellationToken).ConfigureAwait(false))
        {
            messages.Add("Username already taken by another player.");
        }

        if (messages.Count > 0)
        {
            errors["username"] = messages;
        }
    }

    private async Task ValidateEmailAsync(
        string email,
        Dictionary<string, List<string>> errors,
        CancellationToken cancellationToken)
    {
        if (!EmailRegex().IsMatch(email))
        {
            errors["user_email"] = ["Invalid email syntax."];
            return;
        }

        if (await _users.EmailExistsAsync(email, cancellationToken).ConfigureAwait(false))
        {
            errors["user_email"] = ["Email already taken by another player."];
        }
    }

    private static void ValidatePassword(string password, Dictionary<string, List<string>> errors)
    {
        var messages = new List<string>();

        if (password.Length is < 8 or > 32)
        {
            messages.Add("Must be 8-32 characters in length.");
        }

        if (password.Distinct().Count() <= 3)
        {
            messages.Add("Must have more than 3 unique characters.");
        }

        if (messages.Count > 0)
        {
            errors["password"] = messages;
        }
    }
}
