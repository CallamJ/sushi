namespace Sushi.Transpilation;

using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
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
        var dependencies = moduleLoader.OrderedModules
            .Where(module => root == null || module.SourcePath != root.SourcePath)
            .Select(module => module.SourcePath)
            .ToArray();
        if (root == null || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new TranspileResult { Success = false, Diagnostics = diagnostics, DependencyPaths = dependencies };

        var ir = new IrProgram();
        foreach (var module in moduleLoader.OrderedModules)
        {
            var prefix = module == root ? "" : $"sushi_module_{SanitizeModuleName(module.BoxName!)}_";
            var externalSymbols = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var import in module.Imports)
            {
                var importedPrefix = $"sushi_module_{SanitizeModuleName(import.Value.BoxName!)}_";
                foreach (var exported in import.Value.Exports.Keys)
                    externalSymbols[$"{import.Key}.{exported}"] = importedPrefix + exported;
            }

            var lowerer = new AstToIrLowerer(targetProfile, prefix, externalSymbols, module.Imports.Keys.ToHashSet(StringComparer.Ordinal));
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

    private static string SanitizeModuleName(string name) =>
        Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    private static IBackendEmitter GetEmitter(TargetLanguage targetLanguage)
    {
        return targetLanguage switch
        {
            TargetLanguage.Bash => new BashEmitter(),
            TargetLanguage.Zsh => new ZshEmitter(),
            TargetLanguage.Powershell7 => new PowerShellEmitter(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetLanguage), targetLanguage, "Unsupported target")
        };
    }

    private static TargetProfile GetLegacyProfile(TargetLanguage shell) => shell switch
    {
        TargetLanguage.Zsh => new TargetProfile(shell, TargetPlatform.Macos),
        TargetLanguage.Powershell7 => new TargetProfile(shell, TargetPlatform.Windows),
        _ => new TargetProfile(shell, TargetPlatform.Linux)
    };

}
