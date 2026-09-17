using Sushi.Transpilation.IR;

namespace Sushi.Transpilation.Backends.Posix;

public sealed partial class PosixEmitter
{
    // A non-escaping FileQuery exists only in this compile-time plan table.
    // Terminal calls are rendered straight to find; no target value is needed.
    private sealed record FileQueryPlan(string Root, bool Recursive, string? Match, string? Exclude, string Visibility);
    private readonly Dictionary<string, FileQueryPlan> _fileQueries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _escapingFileQueries = new(StringComparer.Ordinal);

    private void EmitFileQueryDeclaration(string name, IrFileQueryExpression query, bool inFunction)
    {
        var plan = BuildFileQueryPlan(query, inFunction);
        if (_escapingFileQueries.Contains(name))
            EmitEscapingFileQueryPlanDeclaration(name, plan, inFunction, declaration: true);
        else
            _fileQueries[name] = plan;
    }

    private bool EmitFileQueryAliasDeclaration(string name, IrIdentifierExpression source, bool inFunction)
    {
        if (!_fileQueries.TryGetValue(SanitizeVariableName(source.Name), out var plan)) return false;
        if (_escapingFileQueries.Contains(name))
            EmitEscapingFileQueryPlanDeclaration(name, plan, inFunction, declaration: true);
        else
            _fileQueries[name] = plan;
        return true;
    }

    private bool EmitFileQueryAssignment(IrAssignmentExpression assignment, bool inFunction)
    {
        if (assignment.Operator != "=") return false;
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Value is IrFileQueryExpression query)
        {
            var plan = BuildFileQueryPlan(query, inFunction);
            if (_escapingFileQueries.Contains(name))
                EmitEscapingFileQueryPlanDeclaration(name, plan, inFunction, declaration: false);
            else
                _fileQueries[name] = plan;
            return true;
        }
        if (assignment.Value is IrIdentifierExpression source &&
            _fileQueries.TryGetValue(SanitizeVariableName(source.Name), out var sourcePlan))
        {
            if (_escapingFileQueries.Contains(name))
                EmitEscapingFileQueryPlanDeclaration(name, sourcePlan, inFunction, declaration: false);
            else
                _fileQueries[name] = sourcePlan;
            return true;
        }
        return false;
    }

    private void EmitEscapingFileQueryDeclaration(string name, IrFileQueryExpression query, bool inFunction)
    {
        EmitEscapingFileQueryPlanDeclaration(name, BuildFileQueryPlan(query, inFunction), inFunction, declaration: true);
    }

    private void EmitEscapingFileQueryPlanDeclaration(string name, FileQueryPlan plan, bool inFunction, bool declaration)
    {
        var entries = new[]
        {
            $"['_fs_root']={plan.Root}",
            $"['_fs_recursive']={(plan.Recursive ? "true" : "false")}",
            $"['_fs_match']={plan.Match ?? "''"}",
            $"['_fs_exclude']={plan.Exclude ?? "''"}",
            $"['_fs_visibility']={Escape.PosixSingleQuoted(plan.Visibility)}"
        };
        if (declaration)
            EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ", false);
        else
            EmitAssociativeObject(name, entries, string.Empty, true);
        _nativeObjectVariables.Add(name);
        _fileQueries[name] = plan;
    }

    private void CollectEscapingFileQueries(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case IrVariableDeclarationStatement { Initializer: { } initializer }:
                    CollectEscapingFileQueries(initializer);
                    break;
                case IrExpressionStatement expression:
                    CollectEscapingFileQueries(expression.Expression);
                    break;
                case IrReturnStatement { Expression: { } expression }:
                    CollectEscapingFileQueries(expression);
                    break;
                case IrBlockStatement block:
                    CollectEscapingFileQueries(block.Statements);
                    break;
                case IrFunctionDeclarationStatement function:
                    CollectEscapingFileQueries(function.Body.Statements);
                    break;
                case IrIfStatement conditional:
                    CollectEscapingFileQueries(conditional.Condition);
                    CollectEscapingFileQueries(conditional.ThenBlock.Statements);
                    if (conditional.ElseBlock is not null) CollectEscapingFileQueries(conditional.ElseBlock.Statements);
                    break;
                case IrForEachStatement each:
                    CollectEscapingFileQueries(each.Collection);
                    CollectEscapingFileQueries(each.Body.Statements);
                    break;
            }
        }
    }

    private void CollectEscapingFileQueries(IrExpression expression)
    {
        switch (expression)
        {
            case IrCallExpression call:
                foreach (var argument in call.Arguments)
                {
                    if (argument.Value is IrIdentifierExpression identifier && IsFileQuery(identifier))
                        _escapingFileQueries.Add(SanitizeVariableName(identifier.Name));
                    CollectEscapingFileQueries(argument.Value);
                }
                break;
            case IrFileQueryExecutionExpression execution:
                CollectEscapingFileQueries(execution.Query);
                break;
            case IrBinaryExpression binary:
                CollectEscapingFileQueries(binary.Left);
                CollectEscapingFileQueries(binary.Right);
                break;
            case IrIntrinsicCallExpression intrinsic:
                foreach (var argument in intrinsic.Arguments) CollectEscapingFileQueries(argument);
                break;
            case IrMethodCallExpression method:
                CollectEscapingFileQueries(method.Target);
                foreach (var argument in method.Arguments) CollectEscapingFileQueries(argument.Value);
                break;
        }
    }

    private static bool IsFileQuery(IrIdentifierExpression identifier) =>
        identifier.StaticType.Name?.Equals("FileQuery", StringComparison.OrdinalIgnoreCase) == true;

    private bool EmitFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution, bool inFunction,
        bool outputAlreadyDeclared = false)
    {
        if (!TryBuildFileQueryPlan(execution.Query, inFunction, out var plan))
            return EmitDynamicFileQueryExecutionDeclaration(name, execution, inFunction, outputAlreadyDeclared);

        if (!outputAlreadyDeclared) WriteLine($"{(inFunction ? "local -a" : "declare -a")} {name}=()");
        var path = "__sushi_path";
        WriteLine($"if [[ ! -d {plan.Root} ]]; then");
        _indent++;
        WriteLine($"printf 'std.fs.query: directory not found: %s\\n' {plan.Root} >&2");
        WriteLine(inFunction ? "return 1" : "exit 1");
        _indent--;
        WriteLine("fi");
        WriteLine($"while IFS= read -r -d '' {path}; do");
        _indent++;
        WriteLine($"{name}+=(\"${{{path}#./}}\")");
        _indent--;
        WriteLine("done < <(");
        _indent++;
        WriteLine($"cd -- {plan.Root}");
        WriteLine(BuildFindCommand(plan, execution.EntryKind));
        _indent--;
        WriteLine(")");
        _nativeArrayVariables[name] = name;
        _arrayInitializers.Remove(name);
        return true;
    }

    private string BuildFindCommand(FileQueryPlan plan, IrFileQueryEntryKind kind)
    {
        var parts = new List<string> { "find -P . -mindepth 1" };
        if (!plan.Recursive) parts.Add("-prune");
        if (kind == IrFileQueryEntryKind.Files) parts.Add("-type f");
        if (kind == IrFileQueryEntryKind.Directories) parts.Add("-type d");
        if (plan.Visibility == "visible") parts.Add("! -name '.*'");
        if (plan.Visibility == "hidden") parts.Add("-name '.*'");
        if (plan.Match is not null) parts.Add($"-name {plan.Match}");
        if (plan.Exclude is not null) parts.Add($"! -name {plan.Exclude}");
        parts.Add("-print0");
        return string.Join(" ", parts);
    }

    private FileQueryPlan BuildFileQueryPlan(IrFileQueryExpression query, bool inFunction) => new(
        PrepareFileQueryValue(query.Root, inFunction),
        ResolveRecursive(query.Recursive),
        ResolveOptionalValue(query.MatchPattern, "_fs_match", inFunction),
        ResolveOptionalValue(query.ExcludePattern, "_fs_exclude", inFunction),
        ResolveVisibility(query.Visibility));

    private bool TryBuildFileQueryPlan(IrExpression query, bool inFunction, out FileQueryPlan plan)
    {
        if (query is IrFileQueryExpression literal)
        {
            plan = BuildFileQueryPlan(literal, inFunction);
            return true;
        }
        if (query is IrIdentifierExpression identifier && _fileQueries.TryGetValue(SanitizeVariableName(identifier.Name), out plan!))
            return true;
        plan = null!;
        return false;
    }

    private string PrepareFileQueryValue(IrExpression expression, bool inFunction)
    {
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: var member } &&
            _fileQueries.TryGetValue(SanitizeVariableName(identifier.Name), out var plan))
            return member switch
            {
                "_fs_root" => plan.Root,
                "_fs_recursive" => plan.Recursive ? "true" : "false",
                "_fs_match" => plan.Match ?? "''",
                "_fs_exclude" => plan.Exclude ?? "''",
                "_fs_visibility" => Escape.PosixSingleQuoted(plan.Visibility),
                _ => PrepareValue(expression, inFunction)
            };
        return PrepareValue(expression, inFunction);
    }

    private bool ResolveRecursive(IrExpression expression)
    {
        if (expression is IrLiteralExpression { Value: true }) return true;
        return expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: "_fs_recursive" } &&
               _fileQueries.TryGetValue(SanitizeVariableName(identifier.Name), out var plan) && plan.Recursive;
    }

    private string? ResolveOptionalValue(IrExpression expression, string member, bool inFunction)
    {
        if (IsNull(expression)) return null;
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: var memberName } &&
            memberName == member && _fileQueries.TryGetValue(SanitizeVariableName(identifier.Name), out var plan))
            return member == "_fs_match" ? plan.Match : plan.Exclude;
        return PrepareFileQueryValue(expression, inFunction);
    }

    private string ResolveVisibility(IrExpression expression)
    {
        if (expression is IrLiteralExpression { Value: string visibility }) return visibility;
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: "_fs_visibility" } &&
            _fileQueries.TryGetValue(SanitizeVariableName(identifier.Name), out var plan))
            return plan.Visibility;
        return "visible";
    }

    private static bool IsNull(IrExpression expression) => expression is IrLiteralExpression { Value: null };

    // A query received through a typed function parameter has no statically
    // visible configuration. Preserve the existing native-object fallback for
    // that boundary; ordinary source queries never reach it.
    private bool EmitDynamicFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution, bool inFunction,
        bool outputAlreadyDeclared)
    {
        var id = ++_valueTempId;
        var prefix = $"__sushi_query_{id}";
        var declaration = inFunction ? "local " : "declare ";
        var resultName = outputAlreadyDeclared && _dialect.IsZsh ? $"{prefix}_results" : name;
        if (!outputAlreadyDeclared) WriteLine($"{declaration}-a {resultName}=()");
        else if (_dialect.IsZsh) WriteLine($"local -a {resultName}=()");
        var root = FileQueryField(execution.Query, "_fs_root");
        var recursive = FileQueryShellField(execution.Query, "_fs_recursive");
        var match = FileQueryShellField(execution.Query, "_fs_match");
        var exclude = FileQueryShellField(execution.Query, "_fs_exclude");
        var visibility = FileQueryShellField(execution.Query, "_fs_visibility");
        WriteLine($"{declaration}{prefix}_cwd=$PWD {prefix}_path {prefix}_entry");
        WriteLine($"if ! cd -- {root}; then");
        _indent++;
        WriteLine($"printf 'std.fs.query: directory not found: %s\\n' {root} >&2");
        WriteLine(inFunction ? "return 1" : "exit 1");
        _indent--;
        WriteLine("fi");
        WriteLine($"while IFS= read -r -d '' {prefix}_path; do");
        _indent++;
        // Portable find starts relative paths with './'. Normalize that small
        // presentation prefix while avoiding an absolute-path/base dance.
        WriteLine($"{prefix}_path=\"${{{prefix}_path#./}}\"");
        WriteLine($"[[ {recursive} == true || \"${{{prefix}_path}}\" != */* ]] || continue");
        WriteLine($"{prefix}_entry=\"${{{prefix}_path##*/}}\"");
        if (execution.EntryKind == IrFileQueryEntryKind.Files) WriteLine($"[[ -f \"${{{prefix}_path}}\" ]] || continue");
        if (execution.EntryKind == IrFileQueryEntryKind.Directories) WriteLine($"[[ -d \"${{{prefix}_path}}\" ]] || continue");
        WriteLine($"if [[ {visibility} == visible && \"${{{prefix}_entry}}\" == .* ]]; then continue; fi");
        WriteLine($"if [[ {visibility} == hidden && \"${{{prefix}_entry}}\" != .* ]]; then continue; fi");
        var queryName = execution.Query is IrIdentifierExpression identifier
            ? SanitizeVariableName(identifier.Name)
            : throw new InvalidOperationException("Dynamic FileQuery execution requires an identifier value.");
        var matchTest = _dialect.IsZsh
            ? $"\"${{{prefix}_entry}}\" != ${{~{queryName}[_fs_match]}}"
            : $"\"${{{prefix}_entry}}\" != ${{{queryName}[_fs_match]}}";
        var excludeTest = _dialect.IsZsh
            ? $"\"${{{prefix}_entry}}\" == ${{~{queryName}[_fs_exclude]}}"
            : $"\"${{{prefix}_entry}}\" == ${{{queryName}[_fs_exclude]}}";
        WriteLine($"if [[ -n \"{match}\" && {matchTest} ]]; then continue; fi");
        WriteLine($"if [[ -n \"{exclude}\" && {excludeTest} ]]; then continue; fi");
        WriteLine($"{resultName}+=(\"${{{prefix}_path}}\")");
        _indent--;
        WriteLine("done < <(find -P . -mindepth 1 -print0)");
        WriteLine($"cd -- \"${{{prefix}_cwd}}\"");
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
                "_fs_root" => literal.Root, "_fs_recursive" => literal.Recursive,
                "_fs_match" => literal.MatchPattern, "_fs_exclude" => literal.ExcludePattern,
                "_fs_visibility" => literal.Visibility, _ => new IrLiteralExpression(null)
            };
            return PrepareValue(value, _currentFunctionName != null);
        }
        return PrepareValue(new IrMemberAccessExpression(query, field, IrTypeRef.Primitive("string")), _currentFunctionName != null);
    }

    private string FileQueryShellField(IrExpression query, string field) => query is IrIdentifierExpression identifier
        ? $"${{{SanitizeVariableName(identifier.Name)}[{field}]-}}"
        : FileQueryField(query, field);
}
