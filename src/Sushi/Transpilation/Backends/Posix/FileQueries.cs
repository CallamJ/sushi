using Sushi.Transpilation.IR;

namespace Sushi.Transpilation.Backends.Posix;

public sealed partial class PosixEmitter
{
    private void EmitFileQueryDeclaration(string name, IrFileQueryExpression query, bool inFunction)
    {
        var entries = new[]
        {
            $"['_fs_root']={PrepareValue(query.Root, inFunction)}",
            $"['_fs_recursive']={PrepareValue(query.Recursive, inFunction)}",
            $"['_fs_match']={PrepareValue(query.MatchPattern, inFunction)}",
            $"['_fs_exclude']={PrepareValue(query.ExcludePattern, inFunction)}",
            $"['_fs_visibility']={PrepareValue(query.Visibility, inFunction)}"
        };
        EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
    }

    private bool EmitFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution, bool inFunction,
        bool outputAlreadyDeclared = false)
    {
        var id = ++_valueTempId;
        var prefix = $"__sushi_query_{id}";
        var declaration = inFunction ? "local " : "declare ";
        var arrayDeclaration = inFunction ? "local " : "declare ";
        var resultName = outputAlreadyDeclared && _dialect.IsZsh ? $"{prefix}_results" : name;
        var root = FileQueryField(execution.Query, "_fs_root");
        var recursive = FileQueryField(execution.Query, "_fs_recursive");
        var match = FileQueryField(execution.Query, "_fs_match");
        var exclude = FileQueryField(execution.Query, "_fs_exclude");
        var visibility = FileQueryField(execution.Query, "_fs_visibility");

        if (!outputAlreadyDeclared) WriteLine($"{arrayDeclaration}-a {resultName}=()");
        else if (_dialect.IsZsh) WriteLine($"local -a {resultName}=()");
        WriteLine($"{declaration}{prefix}_root={root}");
        WriteLine($"{declaration}{prefix}_base");
        WriteLine($"{prefix}_base=$(cd -- \"${{{prefix}_root:-}}\" && pwd -P) || {{ printf 'std.fs.query: directory not found: %s\\n' \"${{{prefix}_root:-}}\" >&2; {(inFunction ? "return 1" : "exit 1")}; }}");
        WriteLine($"{declaration}{prefix}_recursive={recursive}");
        WriteLine($"{declaration}{prefix}_match={match}");
        WriteLine($"{declaration}{prefix}_exclude={exclude}");
        WriteLine($"{declaration}{prefix}_visibility={visibility}");
        WriteLine($"{declaration}{prefix}_path {prefix}_relative {prefix}_entry");
        WriteLine($"while IFS= read -r -d '' {prefix}_path; do");
        _indent++;
        WriteLine($"{prefix}_relative=\"${{{prefix}_path#\"${{{prefix}_base}}/\"}}\"");
        WriteLine($"[[ \"${{{prefix}_recursive:-false}}\" == true || \"${{{prefix}_relative}}\" != */* ]] || continue");
        WriteLine($"{prefix}_entry=\"${{{prefix}_relative##*/}}\"");
        switch (execution.EntryKind)
        {
            case IrFileQueryEntryKind.Files:
                WriteLine($"[[ -f \"${{{prefix}_path}}\" ]] || continue");
                break;
            case IrFileQueryEntryKind.Directories:
                WriteLine($"[[ -d \"${{{prefix}_path}}\" ]] || continue");
                break;
        }
        WriteLine($"if [[ \"${{{prefix}_visibility:-visible}}\" == visible && \"${{{prefix}_entry}}\" == .* ]]; then continue; fi");
        WriteLine($"if [[ \"${{{prefix}_visibility:-visible}}\" == hidden && \"${{{prefix}_entry}}\" != .* ]]; then continue; fi");
        var matchTest = _dialect.IsZsh
            ? $"\"${{{prefix}_entry}}\" != ${{~{prefix}_match}}"
            : $"\"${{{prefix}_entry}}\" != ${{{prefix}_match}}";
        var excludeTest = _dialect.IsZsh
            ? $"\"${{{prefix}_entry}}\" == ${{~{prefix}_exclude}}"
            : $"\"${{{prefix}_entry}}\" == ${{{prefix}_exclude}}";
        WriteLine($"if [[ -n \"${{{prefix}_match:-}}\" && {matchTest} ]]; then continue; fi");
        WriteLine($"if [[ -n \"${{{prefix}_exclude:-}}\" && {excludeTest} ]]; then continue; fi");
        WriteLine($"{resultName}+=(\"${{{prefix}_relative}}\")");
        _indent--;
        WriteLine($"done < <(find -P \"${{{prefix}_base}}\" -mindepth 1 -print0)");
        if (outputAlreadyDeclared && _dialect.IsZsh)
        {
            WriteLine($"typeset -ga ${{{name}}}");
            WriteLine($"set -A ${{{name}}} \"${{{resultName}[@]}}\"");
        }
        _nativeArrayVariables[name] = name;
        _arrayInitializers.Remove(name);
        return true;
    }

    private string FileQueryField(IrExpression query, string field)
    {
        if (query is IrFileQueryExpression literal)
        {
            var value = field switch
            {
                "_fs_root" => literal.Root,
                "_fs_recursive" => literal.Recursive,
                "_fs_match" => literal.MatchPattern,
                "_fs_exclude" => literal.ExcludePattern,
                "_fs_visibility" => literal.Visibility,
                _ => new IrLiteralExpression(null)
            };
            return PrepareValue(value, _currentFunctionName != null);
        }
        return PrepareValue(new IrMemberAccessExpression(query, field, IrTypeRef.Primitive("string")), _currentFunctionName != null);
    }
}
