namespace Sushi.Application.Commands;

using System.CommandLine;
using Sushi.Application;
using Sushi.Application.Console;

static class TranspileCommand
{
    public static Command Create()
    {
        Argument<string> fileArgument = new("file") { Description = "Path to the .sushi file to transpile" };
        var targetOption = CreateTargetOption();
        Option<string?> outputOption = new("-o", "--output") { Description = "Output path (defaults beside the input)" };
        Option<bool> verboseOption = new("-v", "--verbose") { Description = "Print additional diagnostic details" };
        AddFileValidator(fileArgument);
        var command = new Command("transpile", "Transpile a .sushi file to an output file") { fileArgument, targetOption, outputOption, verboseOption };
        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument) ?? "";
            var targetText = parseResult.GetValue(targetOption) ?? "auto";
            if (!CommandSupport.TryParseTarget(targetText, out var target))
            {
                System.Console.Error.WriteLine($"Invalid target '{targetText}'. Choose one of: {TargetProfile.AcceptedValues}.");
                return 1;
            }
            if (!CommandSupport.TryReadSourceFile(filePath, out var source)) return 1;
            var result = CommandSupport.Transpile(filePath, target, source);
            var verbose = parseResult.GetValue(verboseOption);
            if (!result.Success || result.EmittedCode == null)
            {
                DiagnosticPrinter.Print(result.Diagnostics, includeWarnings: verbose);
                return 1;
            }
            var output = parseResult.GetValue(outputOption);
            var outputPath = output ?? CommandSupport.GetOutputPath(filePath, target, includeProfile: !targetText.Equals("auto", StringComparison.OrdinalIgnoreCase));
            if (!CommandSupport.TryWriteOutput(outputPath, result.EmittedCode)) return 1;
            if (verbose && result.Diagnostics.Count > 0) DiagnosticPrinter.Print(result.Diagnostics, includeWarnings: true);
            System.Console.WriteLine($"Transpiled {filePath} -> {outputPath} ({target.Id})");
            return 0;
        });
        return command;
    }

    internal static Option<string> CreateTargetOption() => new("-t", "--target")
    {
        Description = $"Target profile: {TargetProfile.AcceptedValues}",
        DefaultValueFactory = _ => "auto"
    };

    internal static void AddFileValidator(Argument<string> fileArgument) => fileArgument.Validators.Add(result =>
    {
        var value = result.GetValueOrDefault<string>() ?? "";
        if (string.IsNullOrWhiteSpace(value)) result.AddError("File path cannot be empty.");
        else if (!value.EndsWith(".sushi", StringComparison.OrdinalIgnoreCase)) result.AddError("File must have a .sushi extension.");
    });
}
