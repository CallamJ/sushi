namespace Sushi.Application.Commands;

using System.CommandLine;

static class CheckCommand
{
    public static Command Create()
    {
        Argument<string> fileArgument = new("file")
        {
            Description = "Path to the .sushi file to check"
        };

        Option<bool> strictOption = new("--strict")
        {
            Description = "Enable strict type checking",
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
            if (!value.EndsWith(".sushi"))
            {
                result.AddError("File must have a .sushi extension.");
            }
        });

        var command = new Command("check", "Validate a .sushi file without emitting an output file")
        {
            fileArgument,
            strictOption
        };

        command.SetAction(parseResult =>
        {
            // TODO: Implement
        });

        return command;
    }
}

