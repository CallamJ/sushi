namespace Sushi.Application.Commands;

using System.Runtime.InteropServices;
using System.CommandLine;
using Sushi.Application.Console;
using Sushi.Transpilation;


static class TranspileCommand
{
    public static TargetLanguage GetTarget()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TargetLanguage.Powershell7;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TargetLanguage.Zsh;
        }

        return TargetLanguage.Bash;
    }

    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to transpile"
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language to transpile to",
            DefaultValueFactory = parseResult => GetTarget()
        };

        Option<bool> verboseOption = new("-v", "--verbose")
        {
            Description = "Print additional diagnostic details",
            DefaultValueFactory = parseResult => false
        };

        fileArgument.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("File path cannot be empty.");
                return;
            }
            if (!value.EndsWith(".sushi", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("File must have a .sushi extension.");
            }
        });

        var command = new Command("transpile", "Transpile a .sushi file to an output file")
        {
            fileArgument,
            targetLanguageOption,
            verboseOption,
        };

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument) ?? "";
            var target = parseResult.GetValue(targetLanguageOption);
            var verbose = parseResult.GetValue(verboseOption);

            if (!File.Exists(filePath))
            {
                System.Console.Error.WriteLine($"Input file not found: {filePath}");
                return 1;
            }

            string source;
            try
            {
                source = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Failed to read input file: {ex.Message}");
                return 1;
            }

            var transpiler = new Transpiler();
            var result = transpiler.Transpile(new TranspileRequest
            {
                SourceText = source,
                SourcePath = filePath,
                TargetLanguage = target
            });

            if (!result.Success || result.EmittedCode == null)
            {
                DiagnosticPrinter.Print(result.Diagnostics, includeWarnings: verbose);
                return 1;
            }

            var outputPath = GetOutputPath(filePath, target);
            try
            {
                File.WriteAllText(outputPath, result.EmittedCode);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Failed to write output file: {ex.Message}");
                return 1;
            }

            if (verbose && result.Diagnostics.Count > 0)
            {
                DiagnosticPrinter.Print(result.Diagnostics, includeWarnings: true);
            }

            System.Console.WriteLine($"Transpiled {filePath} -> {outputPath}");
            return 0;
        });

        return command;
    }

    private static string GetOutputPath(string inputPath, TargetLanguage target)
    {
        var extension = target switch
        {
            TargetLanguage.Bash => ".sh",
            TargetLanguage.Zsh => ".zsh",
            _ => ".ps1"
        };
        var directory = Path.GetDirectoryName(inputPath) ?? ".";
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, fileNameWithoutExtension + extension);
    }
}
