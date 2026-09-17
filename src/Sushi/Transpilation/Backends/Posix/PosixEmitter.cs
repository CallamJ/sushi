namespace Sushi.Transpilation.Backends.Posix;

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed partial class PosixEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1100";
    private const string AmbiguousShapeCode = "SUSHI1030";

    private readonly GeneratedDocument _document = new();
    private readonly PosixDialect _dialect;
    private EmitContext _context = null!;
    private int _indent { get => _document.Indent; set => _document.Indent = value; }
    private int _valueTempId;
    private string? _currentFunctionName;
    private IrFunctionRole _currentFunctionRole = IrFunctionRole.Function;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _knownIntegerVariables = new(StringComparer.Ordinal);
    private HashSet<string> _knownFloatVariables = new(StringComparer.Ordinal);
    private HashSet<string> _integerReturningFunctions = new(StringComparer.Ordinal);
    private HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeArrayVariables = new(StringComparer.Ordinal);
    private HashSet<string> _nativeObjectVariables = new(StringComparer.Ordinal);
    private HashSet<string> _recordVariables = new(StringComparer.Ordinal);
    private Dictionary<string, IrArrayLiteralExpression> _arrayInitializers = new(StringComparer.Ordinal);
    private Dictionary<string, IrFunctionDeclarationStatement> _functions = new(StringComparer.Ordinal);
    private HashSet<string> _integerArrayVariables = new(StringComparer.Ordinal);
    private Dictionary<string, string> _zshObjectParameterNames = new(StringComparer.Ordinal);
    private HashSet<string> _zshReadOnlyObjectParameters = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeObjectAliases = new(StringComparer.Ordinal);
    private TargetNameAllocator _names = null!;
    private Dictionary<string, string> _generatedFunctionNames = new(StringComparer.Ordinal);
    private bool _currentFunctionReturnsValue;
    private string _currentOutputName = "";
    private bool _needsDynamicMethodMetadata;
    private HashSet<string> _commentedTypes = new(StringComparer.Ordinal);
    private HashSet<string> _classTypeNames = new(StringComparer.Ordinal);
    private HashSet<string> _enumTypeNames = new(StringComparer.Ordinal);
    private bool _emittedTopLevelSection;
    private Dictionary<string, int> _positionalParameterReferences = new(StringComparer.Ordinal);
    private bool _nativeGlobHelper;

    public PosixEmitter(PosixDialect? dialect = null) => _dialect = dialect ?? PosixDialect.Bash;

    public string Emit(IrProgram program, EmitContext context)
    {
        _document.Clear();
        _names = new TargetNameAllocator(_dialect);
        _generatedFunctionNames.Clear();
        _context = context;
        _valueTempId = 0;
        _currentFunctionName = null;
        _currentFunctionRole = IrFunctionRole.Function;
        _currentFunctionReturnType = IrTypeRef.Any;
        _knownIntegerVariables.Clear();
        _knownFloatVariables.Clear();
        _nativeArrayVariables.Clear();
        _nativeObjectVariables.Clear();
        _recordVariables.Clear();
        _arrayInitializers.Clear();
        _functions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        _needsDynamicMethodMetadata = program.Statements.Any(ContainsDynamicMethodDispatch);
        _integerArrayVariables.Clear();
        _zshObjectParameterNames.Clear();
        _zshReadOnlyObjectParameters.Clear();
        _nativeObjectAliases.Clear();
        _commentedTypes.Clear();
        _classTypeNames = CollectClassTypeNames(program.Statements);
        _enumTypeNames = CollectEnumTypeNames(program.Statements);
        _emittedTopLevelSection = false;
        _positionalParameterReferences.Clear();
        _nativeGlobHelper = HasFsGlobImport(program);
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

        WriteLine(_dialect.Shebang);
        if (_dialect.IsZsh)
        {
            WriteLine("set -eu");
            WriteLine("set -o pipefail");
            if (_dialect.SupportsKshArrays && EmissionCapabilityAnalyzer.UsesArrays(program))
            {
                WriteLine("setopt ksharrays");
    }
            }
        else
        {
            WriteLine("set -euo pipefail");
        }
        foreach (var import in program.Statements.OfType<IrStandardLibraryImportStatement>())
        {
            var importText = import.Members.Count == 0
                ? import.Module
                : $"{import.Module}.{{{string.Join(", ", import.Members)}}}";
            if (!string.IsNullOrWhiteSpace(import.Alias)) importText += $" as {import.Alias}";
            WriteLine($"# use {importText}");
        }
        if (EmissionCapabilityAnalyzer.UsesFsGlob(program) && HasFsGlobImport(program))
        {
            EmitNativeGlobHelper();
        }
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return _document.ToString();
    }

    private static bool HasFsGlobImport(IrProgram program)
    {
        var imports = program.Statements.OfType<IrStandardLibraryImportStatement>().ToList();
        return imports.Any(import => (import.Module.Equals("std.fs", StringComparison.Ordinal) || import.Module.Equals("std.fs.glob", StringComparison.Ordinal)) &&
            (import.Members.Count == 0 || import.Members.Contains("glob", StringComparer.Ordinal)));
    }

    private void EmitNativeGlobHelper()
    {
        if (_dialect.IsZsh)
        {
            AppendStdlibHelperBlock("""
__sushi_fs_glob_into() {
  local out_name="${1-}" pattern="${2-}" cwd="${3-}" base="${PWD}" candidate
  typeset -n output="$out_name"
  output=()
  [[ -n "$cwd" && "$cwd" != 'null' ]] && base="$(cd -- "$cwd" && pwd -P)" || true
  [[ -d "$base" ]] || { print -u2 "std.fs.glob: directory not found: $cwd"; return 1; }
  local -a matches
  matches=( ${(N)~base/$pattern} )
  local item
  local -a ordered
  ordered=( "${matches[@]#$base/}" )
  output=( ${(on)ordered} )
}
""");
            return;
        }
        AppendStdlibHelperBlock("""
__sushi_fs_glob_into() {
  local out_name="${1-}" pattern="${2-}" cwd="${3-}" base="${PWD}" candidate
  local -n output="$out_name"
  output=()
  [[ -n "$cwd" && "$cwd" != 'null' ]] && base="$(cd -- "$cwd" && pwd -P)" || true
  [[ -d "$base" ]] || { printf 'std.fs.glob: directory not found: %s\n' "$cwd" >&2; return 1; }
  shopt -s globstar nullglob
  local -a matches=( "$base"/$pattern )
  for candidate in "${matches[@]}"; do
    [[ -e "$candidate" ]] || continue
    output+=("${candidate#$base/}")
  done
  IFS=$'\n' output=( $(printf '%s\n' "${output[@]}" | LC_ALL=C sort) )
}
""");
    }

    private void AppendStdlibHelperBlock(string text)
    {
        _document.Template(text);
    }
}
