namespace Sushi.Transpilation.Backends.PowerShell;

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed partial class PowerShellEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1200";
    private const string AmbiguousShapeCode = "SUSHI1030";

    private readonly GeneratedDocument _document = new();
    private EmitContext _context = null!;
    private int _indent { get => _document.Indent; set => _document.Indent = value; }
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _knownIntegerVariables = new(StringComparer.Ordinal);
    private HashSet<string> _knownFloatVariables = new(StringComparer.Ordinal);
    private HashSet<string> _integerReturningFunctions = new(StringComparer.Ordinal);
    private HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal);
    private Dictionary<string, IrArrayLiteralExpression> _arrayInitializers = new(StringComparer.Ordinal);
    private TargetNameAllocator _names = null!;
    private Dictionary<string, string> _generatedFunctionNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeClassNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeEnumNames = new(StringComparer.Ordinal);
    private Dictionary<string, IrClassDeclarationStatement> _nativeClasses = new(StringComparer.Ordinal);
    private Dictionary<string, IrClassMethod> _nativeMethods = new(StringComparer.Ordinal);
    private Dictionary<string, (string Type, string Value, int Ordinal)> _nativeEnumValues = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeEnumVariableTypes = new(StringComparer.Ordinal);
    private Dictionary<string, (string Type, string Value)> _richEnumValues = new(StringComparer.Ordinal);
    private Dictionary<string, string> _richEnumVariableTypes = new(StringComparer.Ordinal);
    private string? _currentRichEnumReceiver;
    private string? _fallbackReceiverName;
    private bool _nativeGlobHelper;

    public string Emit(IrProgram program, EmitContext context)
    {
        _document.Clear();
        _names = new TargetNameAllocator(TargetLanguage.Powershell51);
        _generatedFunctionNames.Clear();
        _nativeClassNames.Clear();
        _nativeEnumNames.Clear();
        _nativeClasses.Clear();
        _nativeMethods.Clear();
        _nativeEnumValues.Clear();
        _nativeEnumVariableTypes.Clear();
        _richEnumValues.Clear();
        _richEnumVariableTypes.Clear();
        _currentRichEnumReceiver = null;
        _fallbackReceiverName = null;
        _nativeGlobHelper = ExplicitStdlibHelpers.RequiresFsGlob(program);
        _context = context;
        _currentFunctionName = null;
        _currentFunctionReturnType = IrTypeRef.Any;
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        _knownFloatVariables = new HashSet<string>(StringComparer.Ordinal);
        _arrayInitializers.Clear();
        _integerReturningFunctions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .Where(function => function.ReturnType.Kind == IrTypeKind.Primitive &&
                               function.ReturnType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        _floatReturningFunctions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .Where(function => function.ReturnType.Kind == IrTypeKind.Primitive &&
                               function.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);

        WriteLine("Set-StrictMode -Version Latest");
        WriteLine("$ErrorActionPreference = 'Stop'");
        WriteLine("");
        foreach (var import in program.Statements.OfType<IrStandardLibraryImportStatement>())
        {
            var importText = import.Members.Count == 0
                ? import.Module
                : $"{import.Module}.{{{string.Join(", ", import.Members)}}}";
            if (!string.IsNullOrWhiteSpace(import.Alias)) importText += $" as {import.Alias}";
            WriteLine($"# use {importText}");
        }
        if (_nativeGlobHelper)
        {
            EmitFsGlobHelpers();
        }
        var classes = CollectClasses(program.Statements).ToList();
        foreach (var declaration in classes)
        {
            var name = _names.Source(TargetNameKind.Type, declaration.Name);
            _nativeClassNames[declaration.Name] = name;
            _nativeClasses[declaration.Name] = declaration;
            foreach (var method in declaration.Methods.Concat(declaration.Adapters))
                _nativeMethods[method.LegacyName] = method;
        }
        var enums = CollectEnums(program.Statements).ToList();
        foreach (var declaration in enums)
        {
            _nativeEnumNames[declaration.Name] = _names.Source(TargetNameKind.Type, declaration.Name);
            var enumName = _nativeEnumNames[declaration.Name];
            foreach (var value in declaration.Values)
                _nativeEnumValues[$"{declaration.Name}_{value.Name}"] =
                    (enumName, SanitizeMemberName(value.Name), value.Ordinal);
        }
        var richEnums = CollectRichEnums(program.Statements).ToList();
        foreach (var declaration in richEnums)
        {
            _nativeClassNames[declaration.Name] = _names.Source(TargetNameKind.Type, declaration.Name);
            foreach (var value in declaration.Values)
                _richEnumValues[$"{declaration.Name}_{value.Name}"] =
                    (_nativeClassNames[declaration.Name], SanitizeMemberName(value.Name));
            foreach (var method in declaration.Methods.Concat(declaration.Adapters))
                _nativeMethods[method.LegacyName] = method;
        }
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement);
        }

        return _document.ToString();
    }

    // Glob is an explicitly imported stdlib feature. Emit only the two
    // functions required to implement it; the rest of the former runtime
    // bundle must never be pulled into an otherwise native program.
    private void EmitFsGlobHelpers()
    {
        _document.Template(
"""
function __sushi_glob_regex {
    param([string]$pattern)
    $pattern = $pattern.Replace('\\', '/')
    $regex = [System.Text.StringBuilder]::new()
    for ($index = 0; $index -lt $pattern.Length; $index++) {
        $character = $pattern[$index]
        switch ($character) {
            '*' {
                if ($index + 1 -lt $pattern.Length -and $pattern[$index + 1] -eq '*') {
                    if ($index + 2 -lt $pattern.Length -and $pattern[$index + 2] -eq '/') {
                        [void]$regex.Append('([^/]*/)*'); $index += 2
                    } else { [void]$regex.Append('.*'); $index++ }
                } else { [void]$regex.Append('[^/]*') }
            }
            '?' { [void]$regex.Append('[^/]') }
            '[' {
                $end = $pattern.IndexOf(']', $index + 1)
                if ($end -lt 0) { [void]$regex.Append('\\[') }
                else {
                    $class = $pattern.Substring($index + 1, $end - $index - 1)
                    if ($class.StartsWith('!')) { $class = '^' + $class.Substring(1) }
                    [void]$regex.Append('[').Append($class).Append(']'); $index = $end
                }
                }
            default { [void]$regex.Append([regex]::Escape([string]$character)) }
        }
    }

    return '^' + $regex.ToString() + '$'
}

function __sushi_fs_glob {
    param([string]$pattern, [string]$cwd = $null)
    $base = if ([string]::IsNullOrWhiteSpace($cwd)) { (Get-Location).Path } else { (Resolve-Path -LiteralPath $cwd -ErrorAction Stop).Path }
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { throw "std.fs.glob: directory not found: $cwd" }
    $absolute = [System.IO.Path]::IsPathRooted($pattern)
    $matcher = [regex]::new((__sushi_glob_regex $pattern), [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $base -Force -Recurse -ErrorAction Stop)) {
        $full = $item.FullName.Replace('\\', '/')
        $relative = $item.FullName.Substring($base.Length).TrimStart([char]92, [char]47).Replace('\\', '/')
        if ($matcher.IsMatch($(if ($absolute) { $full } else { $relative }))) { $result.Add($relative) }
    }
    $ordered = [string[]]$result.ToArray(); [System.Array]::Sort($ordered, [System.StringComparer]::Ordinal)
    return ,$ordered
}
""");
    }

}
