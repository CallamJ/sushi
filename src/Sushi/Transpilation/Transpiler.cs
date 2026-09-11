namespace Sushi.Transpilation;

using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.Lowering;

public sealed class Transpiler
{
    private const string ParseErrorCode = "SUSHI1000";
    private const string InternalErrorCode = "SUSHI1999";

    public TranspileResult Transpile(TranspileRequest request)
    {
        var diagnostics = new List<Diagnostic>();

        ProgramNode? program;
        try
        {
            var tokenizer = new Tokenizer(request.SourceText);
            var tokens = tokenizer.Tokenize().ToList();
            var lexer = new Lexer(tokens);
            var classifiedTokens = lexer.Lex().ToList();
            var parser = new Parser(classifiedTokens);
            program = parser.Parse();
        }
        catch (Exception ex)
        {
            diagnostics.Add(ParseExceptionToDiagnostic(request.SourcePath, ex));
            return new TranspileResult
            {
                Success = false,
                EmittedCode = null,
                Diagnostics = diagnostics
            };
        }

        var targetProfile = request.TargetProfile ?? GetLegacyProfile(request.TargetLanguage);
        var lowerer = new AstToIrLowerer(targetProfile);
        var ir = lowerer.Lower(program, request.SourcePath);
        diagnostics.AddRange(lowerer.Diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new TranspileResult
            {
                Success = false,
                EmittedCode = null,
                Diagnostics = diagnostics
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
                Diagnostics = diagnostics
            };
        }

        var hasErrors = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        return new TranspileResult
        {
            Success = !hasErrors,
            EmittedCode = hasErrors ? null : code,
            Diagnostics = diagnostics
        };
    }

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

    private static Diagnostic ParseExceptionToDiagnostic(string sourcePath, Exception exception)
    {
        var message = exception.Message;
        var line = 1;
        var column = 1;

        // Parser exceptions frequently include "at line:column"
        var match = Regex.Match(message, @"(?:at|@)\s*(\d+):(\d+)");
        if (match.Success)
        {
            _ = int.TryParse(match.Groups[1].Value, out line);
            _ = int.TryParse(match.Groups[2].Value, out column);
        }

        return Diagnostic.Error(ParseErrorCode, message, new SourceSpan(sourcePath, line, column));
    }
}
