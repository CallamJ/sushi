using Sushi.Transpilation.IR;

namespace Sushi.Transpilation.Backends.PowerShell;

public sealed partial class PowerShellEmitter
{
    private sealed record FileQueryPlan(string Root, bool Recursive, string? Match, string? Exclude, string Visibility);
    private readonly Dictionary<string, FileQueryPlan> _fileQueries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _escapingFileQueries = new(StringComparer.Ordinal);

    private void EmitFileQueryDeclaration(string name, IrFileQueryExpression query)
    {
        var plan = BuildFileQueryPlan(query);
        if (_escapingFileQueries.Contains(name)) EmitEscapingFileQueryPlanDeclaration(name, plan);
        else _fileQueries[name] = plan;
    }

    private bool EmitFileQueryAliasDeclaration(string name, IrIdentifierExpression source)
    {
        if (!_fileQueries.TryGetValue(SanitizeName(source.Name), out var plan)) return false;
        if (_escapingFileQueries.Contains(name)) EmitEscapingFileQueryPlanDeclaration(name, plan);
        else _fileQueries[name] = plan;
        return true;
    }

    private bool EmitFileQueryAssignment(IrAssignmentExpression assignment)
    {
        if (assignment.Operator != "=") return false;
        var name = SanitizeName(assignment.Target.Name);
        if (assignment.Value is IrFileQueryExpression query)
        {
            var plan = BuildFileQueryPlan(query);
            if (_escapingFileQueries.Contains(name)) EmitEscapingFileQueryPlanDeclaration(name, plan);
            else _fileQueries[name] = plan;
            return true;
        }
        if (assignment.Value is IrIdentifierExpression source)
            return EmitFileQueryAliasDeclaration(name, source);
        return false;
    }

    private void EmitEscapingFileQueryDeclaration(string name, IrFileQueryExpression query)
    {
        EmitEscapingFileQueryPlanDeclaration(name, BuildFileQueryPlan(query));
    }

    private void EmitEscapingFileQueryPlanDeclaration(string name, FileQueryPlan plan)
    {
        EmitFileQueryObject(name, plan);
        _fileQueries[name] = plan;
    }

    private void EmitFileQueryObject(string name, FileQueryPlan plan)
    {
        WriteLine($"${name} = [pscustomobject]@{{");
        _indent++;
        WriteLine($"_fs_root = {plan.Root}");
        WriteLine($"_fs_recursive = {(plan.Recursive ? "$true" : "$false")}");
        WriteLine($"_fs_match = {plan.Match ?? "$null"}");
        WriteLine($"_fs_exclude = {plan.Exclude ?? "$null"}");
        WriteLine($"_fs_visibility = '{plan.Visibility}'");
        _indent--;
        WriteLine("}");
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
                        _escapingFileQueries.Add(SanitizeName(identifier.Name));
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

    private void EmitFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution)
    {
        if (!TryBuildFileQueryPlan(execution.Query, out var plan))
        {
            EmitDynamicFileQueryExecutionDeclaration(name, execution);
            return;
        }

        var baseName = $"{name}Base";
        WriteLine($"${baseName} = (Resolve-Path -LiteralPath {plan.Root} -ErrorAction Stop).Path");
        var command = BuildGetChildItemCommand(baseName, plan, execution.EntryKind);
        var filters = BuildPowerShellFilters(plan);
        WriteLine($"${name} = @(");
        _indent++;
        WriteLine(command + " |");
        if (filters.Count > 0)
        {
            _indent++;
            WriteLine($"Where-Object {{ {string.Join(" -and ", filters)} }} |");
            WriteLine($"ForEach-Object {{ $_.FullName.Substring(${baseName}.Length).TrimStart([char]92, [char]47).Replace('\\', '/') }}");
            _indent--;
        }
        else
        {
            WriteLine($"ForEach-Object {{ $_.FullName.Substring(${baseName}.Length).TrimStart([char]92, [char]47).Replace('\\', '/') }}");
        }
        _indent--;
        WriteLine(")");
    }

    private static string BuildGetChildItemCommand(string baseName, FileQueryPlan plan, IrFileQueryEntryKind kind)
    {
        var parts = new List<string> { $"Get-ChildItem -LiteralPath ${baseName}" };
        if (kind == IrFileQueryEntryKind.Files) parts.Add("-File");
        if (kind == IrFileQueryEntryKind.Directories) parts.Add("-Directory");
        if (plan.Recursive) parts.Add("-Recurse");
        parts.Add("-Force");
        if (plan.Match is not null) parts.Add($"-Filter {plan.Match}");
        parts.Add("-ErrorAction Stop");
        return string.Join(" ", parts);
    }

    private static List<string> BuildPowerShellFilters(FileQueryPlan plan)
    {
        var filters = new List<string>
        {
            "($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0"
        };
        if (plan.Visibility == "visible") filters.Add("$_.Name -notlike '.*'");
        if (plan.Visibility == "hidden") filters.Add("$_.Name -like '.*'");
        if (plan.Exclude is not null) filters.Add($"$_.Name -notlike {plan.Exclude}");
        return filters;
    }

    private FileQueryPlan BuildFileQueryPlan(IrFileQueryExpression query) => new(
        EmitFileQueryValue(query.Root),
        ResolveRecursive(query.Recursive),
        ResolveOptionalValue(query.MatchPattern, "_fs_match"),
        ResolveOptionalValue(query.ExcludePattern, "_fs_exclude"),
        ResolveVisibility(query.Visibility));

    private bool TryBuildFileQueryPlan(IrExpression query, out FileQueryPlan plan)
    {
        if (query is IrFileQueryExpression literal)
        {
            plan = BuildFileQueryPlan(literal);
            return true;
        }
        if (query is IrIdentifierExpression identifier && _fileQueries.TryGetValue(SanitizeName(identifier.Name), out plan!))
            return true;
        plan = null!;
        return false;
    }

    private string EmitFileQueryValue(IrExpression expression)
    {
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: var member } &&
            _fileQueries.TryGetValue(SanitizeName(identifier.Name), out var plan))
            return member switch
            {
                "_fs_root" => plan.Root,
                "_fs_recursive" => plan.Recursive ? "$true" : "$false",
                "_fs_match" => plan.Match ?? "$null",
                "_fs_exclude" => plan.Exclude ?? "$null",
                "_fs_visibility" => $"'{plan.Visibility}'",
                _ => EmitValueExpression(expression)
            };
        return EmitValueExpression(expression);
    }

    private bool ResolveRecursive(IrExpression expression)
    {
        if (expression is IrLiteralExpression { Value: true }) return true;
        return expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: "_fs_recursive" } &&
               _fileQueries.TryGetValue(SanitizeName(identifier.Name), out var plan) && plan.Recursive;
    }

    private string? ResolveOptionalValue(IrExpression expression, string member)
    {
        if (IsNull(expression)) return null;
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: var memberName } &&
            memberName == member && _fileQueries.TryGetValue(SanitizeName(identifier.Name), out var plan))
            return member == "_fs_match" ? plan.Match : plan.Exclude;
        return EmitFileQueryValue(expression);
    }

    private string ResolveVisibility(IrExpression expression)
    {
        if (expression is IrLiteralExpression { Value: string visibility }) return visibility;
        if (expression is IrMemberAccessExpression { Target: IrIdentifierExpression identifier, MemberName: "_fs_visibility" } &&
            _fileQueries.TryGetValue(SanitizeName(identifier.Name), out var plan))
            return plan.Visibility;
        return "visible";
    }

    private static bool IsNull(IrExpression expression) => expression is IrLiteralExpression { Value: null };

    // Function parameters are genuine runtime values. Keep the native object
    // representation only for that boundary, not for regular source queries.
    private void EmitDynamicFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution)
    {
        var id = ++_fileQueryTempId;
        var prefix = $"__sushi_query_{id}";
        var query = execution.Query is IrIdentifierExpression identifier
            ? SanitizeName(identifier.Name)
            : throw new InvalidOperationException("Dynamic FileQuery execution requires an identifier value.");
        var itemKind = execution.EntryKind switch
        {
            IrFileQueryEntryKind.Files => " -File",
            IrFileQueryEntryKind.Directories => " -Directory",
            _ => string.Empty
        };
        // Keep the object until filtering is complete so links can be
        // excluded consistently with find -P on Bash and Zsh.
        WriteLine($"${prefix}_base = (Resolve-Path -LiteralPath ${query}._fs_root -ErrorAction Stop).Path");
        WriteLine($"${name} = @(");
        _indent++;
        WriteLine($"Get-ChildItem -LiteralPath ${prefix}_base -Force -Recurse:$([bool]${query}._fs_recursive){itemKind} -ErrorAction Stop |");
        _indent++;
        WriteLine("Where-Object {");
        _indent++;
        WriteLine($"${prefix}_entry = $_.Name");
        WriteLine("($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and");
        WriteLine($"(${query}._fs_visibility -ne 'visible' -or -not ${prefix}_entry.StartsWith('.')) -and");
        WriteLine($"(${query}._fs_visibility -ne 'hidden' -or ${prefix}_entry.StartsWith('.')) -and");
        WriteLine($"(-not ${query}._fs_match -or ${prefix}_entry -like ${query}._fs_match) -and");
        WriteLine($"(-not ${query}._fs_exclude -or ${prefix}_entry -notlike ${query}._fs_exclude)");
        _indent--;
        WriteLine("} |");
        WriteLine($"ForEach-Object {{ $_.FullName.Substring(${prefix}_base.Length).TrimStart([char]92, [char]47).Replace('\\', '/') }}");
        _indent--;
        WriteLine(")");
        _indent--;
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
            return EmitValueExpression(value);
        }
        return EmitValueExpression(new IrMemberAccessExpression(query, field, IrTypeRef.Primitive("string")));
    }

}
