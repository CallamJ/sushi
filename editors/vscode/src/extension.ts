import * as vscode from "vscode";
import { LanguageClient, LanguageClientOptions, ServerOptions, Trace } from "vscode-languageclient/node";

let client: LanguageClient | undefined;

export function activate(context: vscode.ExtensionContext): void {
    const configuration = vscode.workspace.getConfiguration("sushi");
    const command = configuration.get<string>("serverPath", "sushi");
    const targetProfile = configuration.get<string>("targetProfile", "auto");
    const serverOptions: ServerOptions = { command, args: ["lsp", "--stdio"] };
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ language: "sushi", scheme: "file" }],
        initializationOptions: { targetProfile },
        outputChannelName: "Sushi Language Server"
    };
    client = new LanguageClient("sushi", "Sushi Language Server", serverOptions, clientOptions);
    client.setTrace(Trace.fromString(configuration.get<string>("trace.server", "off")));
    void client.start();
    context.subscriptions.push({ dispose: () => { void client?.stop(); } });
}

export async function deactivate(): Promise<void> { await client?.stop(); }
