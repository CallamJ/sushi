namespace Sushi.Application.Commands;

using System.CommandLine;

static class FormatCommand
{
    public static Command Create()
    {
        Argument<string[]> fileArgument = new("files")
        {
            Description = "Path(s) to .sushi file(s) to format",
        };

        Option<bool> checkOption = new("--check")
        {
            Description = "Check if files are formatted without modifying them",
            DefaultValueFactory = parseResult => false
        };

        Option<bool> writeOption = new("-w", "--write")
        {
            Description = "Write formatted output to files",
            DefaultValueFactory = parseResult => true
        };

        fileArgument.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("File path cannot be empty.");
                return;
            }
            if (!value.EndsWith(".sushi"))
            {
                result.AddError("File must have a .sushi extension.");
            }
        });

        var command = new Command("fmt", "Format .sushi files according to standard Sushi style rules")
        {
            fileArgument,
            checkOption,
            writeOption
        };

        command.SetAction(parseResult =>
        {
            // TODO: Implement
        });

        return command;
    }
}
