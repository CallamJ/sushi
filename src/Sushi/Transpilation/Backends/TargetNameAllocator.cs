namespace Sushi.Transpilation.Backends;

using System.Text;
using Sushi.Application;

/// <summary>
/// Allocates readable target identifiers while accounting for target-specific
/// identifier rules and namespaces. Source names retain their preferred spelling;
/// collisions are resolved deterministically with a numeric suffix.
/// </summary>
internal sealed class TargetNameAllocator
{
    private readonly bool _caseInsensitive;
    private readonly HashSet<string> _reserved;
    private readonly Dictionary<(TargetNameKind Kind, string Source), string> _sourceNames = new();
    private readonly Dictionary<TargetNameKind, HashSet<string>> _used = new();

    public TargetNameAllocator(TargetLanguage target, bool zshMode = false)
    {
        _caseInsensitive = target == TargetLanguage.Powershell51;
        _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "args", "input", "true", "false", "null"
        };
        if (zshMode)
        {
            _reserved.UnionWith(new[] { "status", "pipestatus", "_" });
        }
        if (target == TargetLanguage.Powershell51)
        {
            _reserved.UnionWith(new[] { "psitem", "_", "home", "host", "error", "pid", "profile" });
        }
    }

    public TargetNameAllocator(PosixDialect dialect)
        : this(dialect.Language, dialect.IsZsh)
    {
    }

    public string Source(TargetNameKind kind, string sourceName)
    {
        var key = (kind, sourceName);
        if (_sourceNames.TryGetValue(key, out var existing)) return existing;
        var allocated = Allocate(kind, Normalize(sourceName, "value"));
        _sourceNames[key] = allocated;
        return allocated;
    }

    public string Generated(TargetNameKind kind, string preferredName) =>
        Allocate(kind, Normalize(preferredName, "tmp"));

    public string Helper(string preferredName) => Generated(TargetNameKind.Function, "_s_" + preferredName);

    private string Allocate(TargetNameKind kind, string preferred)
    {
        if (!_used.TryGetValue(kind, out var used))
        {
            used = new HashSet<string>(_caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            _used[kind] = used;
        }

        var candidate = _reserved.Contains(preferred) ? preferred + "_2" : preferred;
        var suffix = 2;
        while (!used.Add(candidate)) candidate = preferred + "_" + suffix++;
        return candidate;
    }

    public static string Normalize(string name, string fallback)
    {
        var builder = new StringBuilder(name.Length);
        var previousSeparator = false;
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
                previousSeparator = character == '_';
            }
            else if (!previousSeparator)
            {
                builder.Append('_');
                previousSeparator = true;
            }
        }

        // Keep a deliberate leading underscore.  It is our compact marker for
        // compiler-owned names; trimming it would turn `_tmp` back into a
        // user-looking `tmp`.
        var result = builder.ToString().TrimEnd('_');
        if (result.Length == 0) result = fallback;
        if (char.IsDigit(result[0])) result = "_" + result;
        return result;
    }
}

internal enum TargetNameKind
{
    Variable,
    Function,
    Field,
    Type
}
