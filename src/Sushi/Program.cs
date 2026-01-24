namespace Sushi;

using System.CommandLine;

using Sushi.Application;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("A better way to write shell scripts.");
        CommandRegistry.RegisterCommands(rootCommand);

        return await rootCommand.Parse(args).InvokeAsync();
    }
}
