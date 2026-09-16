namespace Sushi.Transpilation;

using Sushi.Application;
using Sushi.Build;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.Backends.Bash;
using Sushi.Transpilation.Backends.PowerShell;
using Sushi.Transpilation.Backends.Zsh;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Lowering;
using Sushi.Transpilation.Modules;

public sealed class Transpiler
{
    private const string InternalErrorCode = "SUSHI1999";

    public TranspileResult Transpile(TranspileRequest request)
    {
        var diagnostics = new List<Diagnostic>();

        var targetProfile = request.TargetProfile ?? GetLegacyProfile(request.TargetLanguage);
        var moduleLoader = new ModuleGraphLoader(diagnostics);
        var root = moduleLoader.LoadRoot(request.SourcePath, request.SourceText);
        var dependencies = moduleLoader.DependencyPaths;
        if (root == null || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new TranspileResult { Success = false, Diagnostics = diagnostics, DependencyPaths = dependencies };

        var modulePrefixes = AllocateModulePrefixes(moduleLoader.OrderedModules, root);
        var ir = new IrProgram();
        foreach (var module in moduleLoader.OrderedModules)
        {
            diagnostics.AddRange(DocumentationParser.ValidateSource(module.SourceText).Select(issue =>
                Diagnostic.Warning(issue.Code, issue.Message,
                    new SourceSpan(module.SourcePath, issue.Line, issue.Column, issue.Start, issue.Start + 1))));
            var prefix = modulePrefixes[module];
            var externalSymbols = new Dictionary<string, string>(StringComparer.Ordinal);
            var externalFunctions = new Dictionary<string, Sushi.Build.SyntaxTree.FunctionDeclarationNode>(StringComparer.Ordinal);
            var externalClasses = new Dictionary<string, Sushi.Build.SyntaxTree.ClassDeclarationNode>(StringComparer.Ordinal);
            var externalEnums = new Dictionary<string, Sushi.Build.SyntaxTree.EnumDeclarationNode>(StringComparer.Ordinal);
            foreach (var import in module.Imports)
            {
                var importedPrefix = modulePrefixes[import.Value];
                foreach (var exported in import.Value.Exports)
                {
                    var emittedName = importedPrefix + exported.Key;
                    externalSymbols[$"{import.Key}.{exported.Key}"] = emittedName;
                    switch (exported.Value)
                    {
                        case Sushi.Build.SyntaxTree.FunctionDeclarationNode function:
                            externalFunctions[emittedName] = function;
                            break;
                        case Sushi.Build.SyntaxTree.ClassDeclarationNode @class:
                            externalClasses[emittedName] = @class;
                            break;
                        case Sushi.Build.SyntaxTree.EnumDeclarationNode @enum:
                            externalEnums[emittedName] = @enum;
                            break;
                    }
                }
            }

            var lowerer = new AstToIrLowerer(
                targetProfile,
                prefix,
                externalSymbols,
                module.Imports.Keys.ToHashSet(StringComparer.Ordinal),
                externalFunctions,
                externalClasses,
                externalEnums);
            var moduleIr = lowerer.Lower(module.Program, module.SourcePath);
            diagnostics.AddRange(lowerer.Diagnostics);
            ir.Statements.AddRange(moduleIr.Statements);
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new TranspileResult
            {
                Success = false,
                EmittedCode = null,
                Diagnostics = diagnostics,
                DependencyPaths = dependencies
            };
        }

        var emitter = GetEmitter(targetProfile.Shell);
        var emitContext = new EmitContext(request.SourcePath, diagnostics, targetProfile);
        string code;
        try
        {
            code = emitter.Emit(ir, emitContext);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic.Error(
                InternalErrorCode,
                $"Emitter failure: {ex.Message}",
                SourceSpan.Unknown(request.SourcePath)));

            return new TranspileResult
            {
                Success = false,
                EmittedCode = null,
                Diagnostics = diagnostics,
                DependencyPaths = dependencies
            };
        }

        var hasErrors = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        return new TranspileResult
        {
            Success = !hasErrors,
            EmittedCode = hasErrors ? null : code,
            Diagnostics = diagnostics,
            DependencyPaths = dependencies
        };
    }

    private static Dictionary<LoadedModule, string> AllocateModulePrefixes(
        IReadOnlyList<LoadedModule> modules,
        LoadedModule root)
    {
        var prefixes = new Dictionary<LoadedModule, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (module == root)
            {
                prefixes[module] = "";
                continue;
            }

            var baseName = TargetNameAllocator.Normalize(module.BoxName ?? "module", "module").ToLowerInvariant();
            var candidate = baseName;
            var suffix = 2;
            while (!used.Add(candidate)) candidate = baseName + "_" + suffix++;
            prefixes[module] = candidate + "_";
        }
        return prefixes;
    }

    private static IBackendEmitter GetEmitter(TargetLanguage targetLanguage)
    {
        return targetLanguage switch
        {
            TargetLanguage.Bash => new BashEmitter(),
            TargetLanguage.Zsh => new ZshEmitter(),
            TargetLanguage.Powershell51 => new PowerShellEmitter(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetLanguage), targetLanguage, "Unsupported target")
        };
    }

    private static TargetProfile GetLegacyProfile(TargetLanguage shell) => shell switch
    {
        TargetLanguage.Zsh => new TargetProfile(shell, TargetPlatform.Macos),
        TargetLanguage.Powershell51 => new TargetProfile(shell, TargetPlatform.Windows),
        _ => new TargetProfile(shell, TargetPlatform.Linux)
    };

}
