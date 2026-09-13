namespace Sushi.Application.Commands;

using System.CommandLine;
using Sushi.Application.Documentation;
using Sushi.Application.Console;

internal static class DocsCommand
{
    public static Command Create()
    {
        Argument<string> file = new("file") { Description = "Root .sushi module to document" };
        TranspileCommand.AddFileValidator(file);
        Option<string> output = new("-o", "--output") { Description = "Markdown output path", Required = true };
        Option<bool> builtins = new("--include-builtins") { Description = "Include Sushi standard-library functions" };
        var command = new Command("docs", "Generate Markdown API reference from exported Sushi APIs") { file, output, builtins };
        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(file) ?? "";
            if (!CommandSupport.TryReadSourceFile(path, out var source)) return 1;
            if (!ApiDocumentationGenerator.TryGenerate(path, source, parseResult.GetValue(builtins), out var markdown, out var diagnostics))
            {
                DiagnosticPrinter.Print(diagnostics, includeWarnings: true);
                return 1;
            }
            var outputPath = parseResult.GetValue(output) ?? "";
            try
            {
                var fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var temporary = fullPath + ".tmp";
                File.WriteAllText(temporary, markdown);
                File.Move(temporary, fullPath, true);
                if (diagnostics.Count > 0) DiagnosticPrinter.Print(diagnostics, includeWarnings: true);
                System.Console.WriteLine($"Generated API documentation: {fullPath}");
                return 0;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Failed to write documentation: {ex.Message}");
                return 1;
            }
        });
        return command;
    }
}
