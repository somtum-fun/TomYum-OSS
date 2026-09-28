using System.Reflection;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>Who may run a chat command.</summary>
public enum CommandAccess
{
    /// <summary>Anyone (the command may still refuse, e.g. <c>!map</c> for non-BNs).</summary>
    Everyone,

    /// <summary>Staff only: refused here before the command runs.</summary>
    Staff,

    /// <summary>Listed under staff in <c>!help</c>; the command checks the caller itself (moderation's own rules).</summary>
    StaffCheckedByCommand,
}

/// <summary>
/// Marks a method as a chat command (Sunrise-style). The method takes a <see cref="ChatCommandContext"/>
/// and returns <c>string?</c>, <c>Task&lt;string&gt;</c> or <c>Task&lt;string?&gt;</c>. One method may
/// carry several (aliases, or commands that share code); <c>!help</c> is made from these.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ChatCommandAttribute : Attribute
{
    public ChatCommandAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The name without <c>!</c>, lower-case.</summary>
    public string Name { get; }

    /// <summary>The arguments, e.g. <c>&lt;user&gt; &lt;reason&gt;</c>.</summary>
    public string Usage { get; init; } = string.Empty;

    /// <summary>A short note for <c>!help</c>, e.g. <c>Discord</c>.</summary>
    public string Help { get; init; } = string.Empty;

    public CommandAccess Access { get; init; }

    /// <summary>Left out of <c>!help</c>.</summary>
    public bool Hidden { get; init; }
}

/// <summary>A chat command being run.</summary>
/// <param name="Caller">Who sent it.</param>
/// <param name="Name">The command's name as typed (lower-case).</param>
/// <param name="Parts">Every word, the command first (so <c>Parts[1]</c> is the first argument).</param>
/// <param name="RawText">The whole message.</param>
/// <param name="Privileges">The caller's in-game privileges.</param>
/// <param name="CancellationToken">Cancelled when the request is.</param>
public sealed record ChatCommandContext(
    OnlinePlayer Caller, string Name, string[] Parts, string RawText, BanchoPrivileges Privileges, CancellationToken CancellationToken)
{
    /// <summary>The words after the command.</summary>
    public string[] Args => Parts.Length > 1 ? Parts[1..] : [];
}

/// <summary>The <see cref="ChatCommandAttribute"/> methods of a class, found once by reflection.</summary>
public sealed class ChatCommandTable<T>
    where T : class
{
    private readonly Dictionary<string, (ChatCommandAttribute Info, MethodInfo Method)> _commands = new(StringComparer.Ordinal);

    public ChatCommandTable()
    {
        foreach (var method in typeof(T).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var info in method.GetCustomAttributes<ChatCommandAttribute>())
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ChatCommandContext)
                    || !(method.ReturnType == typeof(string) || method.ReturnType == typeof(Task<string>) || method.ReturnType == typeof(Task<string?>)))
                {
                    throw new InvalidOperationException($"{typeof(T).Name}.{method.Name}: a chat command takes a ChatCommandContext and returns a string or Task<string>.");
                }
                if (info.Name.Any(char.IsUpper) || !_commands.TryAdd(info.Name, (info, method)))
                    throw new InvalidOperationException($"Chat command !{info.Name} is declared twice or isn't lower-case.");
            }
        }
    }

    /// <summary>Every command, in declaration order of their names.</summary>
    public IReadOnlyList<ChatCommandAttribute> All => _commands.Values.Select(c => c.Info).ToList();

    public bool Contains(string name) => _commands.ContainsKey(name);

    /// <summary>Runs command <paramref name="context"/>'s name on <paramref name="target"/>; false when there's no such command.</summary>
    public async Task<(bool Found, string? Reply)> RunAsync(T target, ChatCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_commands.TryGetValue(context.Name, out var command))
            return (false, null);

        var result = command.Method.Invoke(command.Method.IsStatic ? null : target, [context]);
        return result switch
        {
            Task<string?> task => (true, await task.ConfigureAwait(false)),
            string text => (true, text),
            _ => (true, null),
        };
    }

    /// <summary>
    /// The <c>!help</c> text: everyone's commands, then (for staff) the staff ones, each with its usage
    /// and note, e.g. <c>!roll [max]</c> or <c>!link &lt;code&gt; (Discord)</c>.
    /// </summary>
    public string Help(bool staff, IEnumerable<string>? extra = null)
    {
        static string Line(ChatCommandAttribute c) =>
            "!" + c.Name + (c.Usage.Length > 0 ? " " + c.Usage : string.Empty) + (c.Help.Length > 0 ? $" ({c.Help})" : string.Empty);

        var visible = _commands.Values.Select(c => c.Info).Where(c => !c.Hidden).ToList();
        var text = "Commands: " + string.Join(", ", visible.Where(c => c.Access == CommandAccess.Everyone).Select(Line)) + ".";
        if (staff)
            text += " Staff: " + string.Join(", ", visible.Where(c => c.Access != CommandAccess.Everyone).Select(Line)) + ". Names with spaces: use _.";
        if (extra?.ToList() is { Count: > 0 } more)
            text += " Plugins: " + string.Join(", ", more) + ".";
        return text;
    }
}
