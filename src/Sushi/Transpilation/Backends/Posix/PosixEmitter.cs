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
    private HashSet<string> _commentedTypes = new(StringComparer.Ordinal);
    private HashSet<string> _classTypeNames = new(StringComparer.Ordinal);
    private HashSet<string> _enumTypeNames = new(StringComparer.Ordinal);
    private bool _emittedTopLevelSection;
    private Dictionary<string, int> _positionalParameterReferences = new(StringComparer.Ordinal);

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
        _integerArrayVariables.Clear();
        _zshObjectParameterNames.Clear();
        _zshReadOnlyObjectParameters.Clear();
        _nativeObjectAliases.Clear();
        _commentedTypes.Clear();
        _classTypeNames = CollectClassTypeNames(program.Statements);
        _enumTypeNames = CollectEnumTypeNames(program.Statements);
        _emittedTopLevelSection = false;
        _positionalParameterReferences.Clear();
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
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return _document.ToString();
    }

}
