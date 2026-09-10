namespace Sushi.Application.Commands;

using System.CommandLine;
using System.Text.Json;
using Sushi.Transpilation;

static class CheckCommand
{
    private sealed record SerializableDiagnostic(
        string Code,
        string Severity,
        string Message,
        string SourcePath,
        int Line,
        int Column);

    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to check"
        };

        Option<TargetLanguage> targetLanguageOption = new("-t", "--target")
        {
            Description = "Language target to validate transpilation against",
            DefaultValueFactory = parseResult => CommandSupport.GetDefaultTarget()
        };

        Option<string> formatOption = new("--format")
        {
            Description = "Output format: plain or json",
            DefaultValueFactory = _ => "plain"
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

        var command = new Command("check", "Validate a .sushi file without emitting an output file")
        {
            fileArgument,
            targetLanguageOption,
            formatOption
        };

        formatOption.Validators.Add(result =>
        {
            var value = (result.GetValueOrDefault<string>() ?? "").Trim().ToLowerInvariant();
            if (value is not ("plain" or "json"))
            {
                result.AddError("Format must be 'plain' or 'json'.");
            }
        });

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument) ?? "";
            var target = parseResult.GetValue(targetLanguageOption);
            var format = (parseResult.GetValue(formatOption) ?? "plain").Trim().ToLowerInvariant();
            if (!CommandSupport.TryReadSourceFile(filePath, out var source))
            {
                return 1;
            }

            var result = CommandSupport.Transpile(filePath, target, source);
            var success = result.Success;

            if (format == "json")
            {
                var payload = new
                {
                    success,
                    target = target.ToString(),
                    diagnostics = result.Diagnostics.Select(d => new SerializableDiagnostic(
                        d.Code,
                        d.Severity.ToString().ToUpperInvariant(),
                        d.Message,
                        d.Span.SourcePath,
                        d.Span.Line,
                        d.Span.Column))
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                System.Console.WriteLine(json);
                return success ? 0 : 1;
            }

            if (!success)
            {
                CommandSupport.PrintDiagnostics(result, verbose: true);
                return 1;
            }

            if (result.Diagnostics.Count > 0)
            {
                CommandSupport.PrintDiagnostics(result, verbose: true);
            }

            System.Console.WriteLine($"Check passed: {filePath} ({target})");
            return 0;
        });

        return command;
    }
}
