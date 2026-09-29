namespace TomYum.Irc;

/// <summary>
/// One IRC protocol line (RFC 1459/2812): an optional <c>:prefix</c>, the command (a word or a
/// three-digit reply) and its parameters, the last of which may contain spaces (<c>:trailing</c>).
/// </summary>
public sealed record IrcLine(string? Prefix, string Command, IReadOnlyList<string> Parameters)
{
    /// <summary>The longest line IRC allows, the CR LF included.</summary>
    public const int MaxLength = 512;

    /// <summary>Parameter <paramref name="index"/>, or an empty string when there are fewer.</summary>
    public string this[int index] => index < Parameters.Count ? Parameters[index] : string.Empty;

    /// <summary>Reads a line (without its CR LF); null when it has no command.</summary>
    public static IrcLine? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rest = line.TrimEnd('\r', '\n').TrimStart(' ');
        string? prefix = null;
        if (rest.StartsWith(':'))
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
                return null;
            prefix = rest[1..space];
            rest = rest[(space + 1)..].TrimStart(' ');
        }

        var parameters = new List<string>();
        string command;
        var commandEnd = rest.IndexOf(' ', StringComparison.Ordinal);
        if (commandEnd < 0)
        {
            command = rest;
            rest = string.Empty;
        }
        else
        {
            command = rest[..commandEnd];
            rest = rest[(commandEnd + 1)..];
        }
        if (command.Length == 0)
            return null;

        while (rest.Length > 0)
        {
            rest = rest.TrimStart(' ');
            if (rest.Length == 0)
                break;
            if (rest.StartsWith(':'))
            {
                parameters.Add(rest[1..]);
                break;
            }
            var end = rest.IndexOf(' ', StringComparison.Ordinal);
            if (end < 0)
            {
                parameters.Add(rest);
                break;
            }
            parameters.Add(rest[..end]);
            rest = rest[(end + 1)..];
        }
        return new IrcLine(prefix, command.ToUpperInvariant(), parameters);
    }

    /// <summary>The line as sent (without CR LF): the last parameter is always written as trailing.</summary>
    public string Format()
    {
        var text = Prefix is null ? Command : $":{Prefix} {Command}";
        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            text += i == Parameters.Count - 1 ? $" :{parameter}" : $" {parameter}";
        }
        return text;
    }

    /// <summary>A nick for a username: IRC nicks have no spaces, so they become underscores (as on osu!).</summary>
    public static string Nick(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        return username.Replace(' ', '_');
    }
}
