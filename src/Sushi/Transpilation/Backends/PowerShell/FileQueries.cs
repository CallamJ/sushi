using Sushi.Transpilation.IR;

namespace Sushi.Transpilation.Backends.PowerShell;

public sealed partial class PowerShellEmitter
{
    private void EmitFileQueryDeclaration(string name, IrFileQueryExpression query)
    {
        WriteLine($"${name} = [pscustomobject]@{{");
        _indent++;
        WriteLine($"_fs_root = {EmitValueExpression(query.Root)}");
        WriteLine($"_fs_recursive = {EmitValueExpression(query.Recursive)}");
        WriteLine($"_fs_match = {EmitValueExpression(query.MatchPattern)}");
        WriteLine($"_fs_exclude = {EmitValueExpression(query.ExcludePattern)}");
        WriteLine($"_fs_visibility = {EmitValueExpression(query.Visibility)}");
        _indent--;
        WriteLine("}");
    }

    private void EmitFileQueryExecutionDeclaration(string name, IrFileQueryExecutionExpression execution)
    {
        var id = ++_fileQueryTempId;
        var prefix = $"__sushi_query_{id}";
        var root = FileQueryField(execution.Query, "_fs_root");
        var recursive = FileQueryField(execution.Query, "_fs_recursive");
        var match = FileQueryField(execution.Query, "_fs_match");
        var exclude = FileQueryField(execution.Query, "_fs_exclude");
        var visibility = FileQueryField(execution.Query, "_fs_visibility");
        WriteLine($"${prefix}_root = [string]({root})");
        WriteLine($"${prefix}_base = (Resolve-Path -LiteralPath ${prefix}_root -ErrorAction Stop).Path");
        WriteLine($"${prefix}_recursive = [bool]({recursive})");
        WriteLine($"${prefix}_match = [string]({match})");
        WriteLine($"${prefix}_exclude = [string]({exclude})");
        WriteLine($"${prefix}_visibility = [string]({visibility})");
        WriteLine($"if (-not (Test-Path -LiteralPath ${prefix}_base -PathType Container)) {{ throw \"std.fs.query: directory not found: ${{{prefix}_root}}\" }}");
        WriteLine($"if (${prefix}_recursive) {{ ${prefix}_items = @(Get-ChildItem -LiteralPath ${prefix}_base -Force -Recurse -ErrorAction Stop) }} else {{ ${prefix}_items = @(Get-ChildItem -LiteralPath ${prefix}_base -Force -ErrorAction Stop) }}");
        WriteLine($"${name} = @()");
        WriteLine($"foreach (${prefix}_item in ${prefix}_items) {{");
        _indent++;
        switch (execution.EntryKind)
        {
            case IrFileQueryEntryKind.Files:
                WriteLine($"if (${prefix}_item.PSIsContainer) {{ continue }}");
                break;
            case IrFileQueryEntryKind.Directories:
                WriteLine($"if (-not ${prefix}_item.PSIsContainer) {{ continue }}");
                break;
        }
        WriteLine($"${prefix}_hidden = ${prefix}_item.Name.StartsWith('.') -or ((${prefix}_item.Attributes -band [IO.FileAttributes]::Hidden) -ne 0)");
        WriteLine($"if (${prefix}_visibility -eq 'visible' -and ${prefix}_hidden) {{ continue }}");
        WriteLine($"if (${prefix}_visibility -eq 'hidden' -and -not ${prefix}_hidden) {{ continue }}");
        WriteLine($"if (${prefix}_match -and ${prefix}_item.Name -notlike ${prefix}_match) {{ continue }}");
        WriteLine($"if (${prefix}_exclude -and ${prefix}_item.Name -like ${prefix}_exclude) {{ continue }}");
        WriteLine($"${prefix}_relative = ${prefix}_item.FullName.Substring(${prefix}_base.Length).TrimStart([char]92, [char]47).Replace('\\', '/')");
        WriteLine($"${name} += ${prefix}_relative");
        _indent--;
        WriteLine("}");
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
            return EmitValueExpression(value);
        }
        return EmitValueExpression(new IrMemberAccessExpression(query, field, IrTypeRef.Primitive("string")));
    }
}
