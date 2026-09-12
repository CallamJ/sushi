namespace Sushi.Application.Commands;

using System.CommandLine;
using Sushi.Application.LanguageServer;

/// <summary>Hosts the editor-facing Language Server Protocol endpoint.</summary>
internal static class LspCommand
{
    public static Command Create()
    {
        var stdio = new Option<bool>("--stdio")
        {
            Description = "Use standard input/output for Language Server Protocol messages.",
            DefaultValueFactory = _ => true
        };
        var command = new Command("lsp", "Start the Sushi Language Server Protocol endpoint") { stdio };
        command.SetAction(async _ =>
        {
            await new SushiLanguageServer(System.Console.OpenStandardInput(), System.Console.OpenStandardOutput(), System.Console.Error).RunAsync();
            return 0;
        });
        return command;
    }
}
