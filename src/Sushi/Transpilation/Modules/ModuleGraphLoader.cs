namespace Sushi.Transpilation.Modules;

using Sushi.Build;
using Sushi.Build.SyntaxTree;

internal sealed record LoadedModule(
    string SourcePath,
    ProgramNode Program,
    string? BoxName,
    IReadOnlyDictionary<string, LoadedModule> Imports,
    IReadOnlyDictionary<string, AstNode> Exports);

internal sealed class ModuleGraphLoader
{
    private readonly Dictionary<string, LoadedModule> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _boxes = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diagnostics;

    public ModuleGraphLoader(List<Diagnostic> diagnostics) => _diagnostics = diagnostics;

    public IReadOnlyList<LoadedModule> OrderedModules { get; private set; } = Array.Empty<LoadedModule>();

    public LoadedModule? LoadRoot(string sourcePath, string sourceText)
    {
        var ordered = new List<LoadedModule>();
        var rootPath = Path.GetFullPath(sourcePath);
        var root = Load(rootPath, sourceText, isRoot: true, ordered);
        OrderedModules = ordered;
        return root;
    }

    private LoadedModule? Load(string sourcePath, string? suppliedSource, bool isRoot, List<LoadedModule> ordered)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (_loaded.TryGetValue(sourcePath, out var existing)) return existing;
        if (!_loading.Add(sourcePath))
        {
            Error("SUSHI1042", $"Module import cycle detected at '{sourcePath}'.", sourcePath);
            return null;
        }

        string source;
        try
        {
            source = suppliedSource ?? File.ReadAllText(sourcePath);
        }
        catch (Exception ex)
        {
            Error("SUSHI1040", $"Unable to read module '{sourcePath}': {ex.Message}", sourcePath);
            _loading.Remove(sourcePath);
            return null;
        }

        ProgramNode program;
        try
        {
            program = new Parser(new Lexer(new Tokenizer(source).Tokenize()).Lex()).Parse();
        }
        catch (Exception ex)
        {
            Error("SUSHI1000", ex.Message, sourcePath);
            _loading.Remove(sourcePath);
            return null;
        }

        var boxes = program.Declarations.OfType<BoxDeclarationNode>().ToList();
        var boxName = boxes.SingleOrDefault()?.FullName;
        if (boxes.Count > 1)
            Error("SUSHI1041", "A module may declare only one box identity.", sourcePath);
        if (!isRoot && boxName == null)
            Error("SUSHI1041", $"Imported module '{sourcePath}' must declare a box identity.", sourcePath);
        if (boxName != null && _boxes.TryGetValue(boxName, out var prior) && prior != sourcePath)
            Error("SUSHI1041", $"Box '{boxName}' is declared by both '{prior}' and '{sourcePath}'.", sourcePath);
        else if (boxName != null)
            _boxes[boxName] = sourcePath;

        var exports = new Dictionary<string, AstNode>(StringComparer.Ordinal);
        foreach (var exported in program.Declarations.OfType<ExportDeclarationNode>())
        {
            var name = GetDeclarationName(exported.Declaration);
            if (name == null || !exports.TryAdd(name, exported.Declaration))
                Error("SUSHI1043", "Exported declarations must have unique names.", sourcePath, exported.Line, exported.Column);
        }

        var imports = new Dictionary<string, LoadedModule>(StringComparer.Ordinal);
        var localNames = program.Declarations
            .Select(item => item is ExportDeclarationNode export ? export.Declaration : item)
            .Select(GetDeclarationName)
            .Where(name => name != null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var use in program.Declarations.OfType<UseDeclarationNode>())
        {
            if (Path.IsPathRooted(use.ImportPath) || !use.ImportPath.EndsWith(".sushi", StringComparison.OrdinalIgnoreCase))
            {
                Error("SUSHI1040", $"Import '{use.ImportPath}' must be a relative .sushi path.", sourcePath, use.Line, use.Column);
                continue;
            }
            var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, use.ImportPath));
            var alias = use.Alias ?? Path.GetFileNameWithoutExtension(targetPath).Replace('-', '_');
            if (imports.ContainsKey(alias) || localNames.Contains(alias))
            {
                Error("SUSHI1044", $"Duplicate module alias '{alias}'.", sourcePath, use.Line, use.Column);
                continue;
            }
            var imported = Load(targetPath, null, isRoot: false, ordered);
            if (imported != null) imports[alias] = imported;
        }

        var module = new LoadedModule(sourcePath, program, boxName, imports, exports);
        _loaded[sourcePath] = module;
        _loading.Remove(sourcePath);
        ordered.Add(module);
        return module;
    }

    private static string? GetDeclarationName(AstNode declaration) => declaration switch
    {
        FunctionDeclarationNode function => function.Name,
        ClassDeclarationNode @class => @class.Name,
        EnumDeclarationNode @enum => @enum.Name,
        VariableDeclarationStatementNode variable => variable.Name,
        _ => null
    };

    private void Error(string code, string message, string path, int line = 1, int column = 1) =>
        _diagnostics.Add(Diagnostic.Error(code, message, new SourceSpan(path, line, column)));
}
