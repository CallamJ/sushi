import * as vscode from "vscode";
import * as fs from "fs/promises";
import * as os from "os";
import * as path from "path";
import { LanguageClient, LanguageClientOptions, ServerOptions, Trace } from "vscode-languageclient/node";

let client: LanguageClient | undefined;
const documentContinuationInProgress = new Set<string>();

interface TranspileResult {
    success: boolean;
    targetProfile: string;
    languageId: string;
    fileExtension: string;
    code: string;
    diagnostics: Array<{ code: string; message: string; severity: string }>;
}

export function activate(context: vscode.ExtensionContext): void {
    void startLanguageClient().catch(error => {
        const message = error instanceof Error ? error.message : String(error);
        vscode.window.showErrorMessage(`Sushi language server could not start: ${message}`);
        vscode.window.createOutputChannel("Sushi Language Server").appendLine(message);
    });
    context.subscriptions.push({ dispose: () => { void client?.stop(); } });
    const checkOutput = vscode.window.createOutputChannel("Sushi Check");
    context.subscriptions.push(checkOutput);
    context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(event => {
        void continueDocumentationComment(event);
        void triggerDocumentationTemplateSuggestion(event);
    }));

    const generated = new Map<string, { code: string; languageId: string }>();
    const provider = vscode.workspace.registerTextDocumentContentProvider("sushi-generated", {
        provideTextDocumentContent(uri): string { return generated.get(uri.toString())?.code ?? ""; }
    });
    context.subscriptions.push(provider);

    context.subscriptions.push(vscode.commands.registerCommand("sushi.checkCurrentBuffer", async (uri?: vscode.Uri) => {
        const result = await transpile(activeSushiDocument(uri));
        checkOutput.clear();
        checkOutput.appendLine(result.success
            ? `Sushi check passed (${result.targetProfile}).`
            : result.diagnostics.map(diagnostic => `${diagnostic.code}: ${diagnostic.message}`).join("\n") || "Sushi check failed.");
        checkOutput.show(true);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("sushi.previewGenerated", async (uri?: vscode.Uri) => {
        const result = await transpile(activeSushiDocument(uri));
        if (!result.success) { vscode.window.showErrorMessage("Sushi could not generate output; fix the reported diagnostics first."); return; }
        const name = activeSushiDocument(uri).uri.path.split("/").pop()?.replace(/\.sushi$/, "") ?? "script";
        const generatedUri = vscode.Uri.parse(`sushi-generated:/${name}.${result.targetProfile}${result.fileExtension}`);
        generated.set(generatedUri.toString(), { code: result.code, languageId: result.languageId });
        const preview = await vscode.workspace.openTextDocument(generatedUri);
        await vscode.languages.setTextDocumentLanguage(preview, result.languageId);
        await vscode.window.showTextDocument(preview, { preview: true, viewColumn: vscode.ViewColumn.Beside });
    }));
    context.subscriptions.push(vscode.commands.registerCommand("sushi.runCurrentBuffer", async (uri?: vscode.Uri) => {
        const result = await transpile(activeSushiDocument(uri));
        if (!result.success) { vscode.window.showErrorMessage("Sushi could not run this buffer; fix the reported diagnostics first."); return; }
        const directory = await fs.mkdtemp(path.join(os.tmpdir(), "sushi-"));
        const script = path.join(directory, `script${result.fileExtension}`);
        await fs.writeFile(script, result.code, "utf8");
        const terminal = vscode.window.createTerminal({ name: `Sushi: ${result.targetProfile}` });
        terminal.show(true);
        terminal.sendText(runCommand(script, directory, result.targetProfile), true);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("sushi.restartLanguageServer", async () => {
        await restartLanguageClient();
        void vscode.window.showInformationMessage("Sushi language server restarted.");
    }));
}

async function continueDocumentationComment(event: vscode.TextDocumentChangeEvent): Promise<void> {
    const document = event.document;
    if (document.languageId !== "sushi" || documentContinuationInProgress.has(document.uri.toString())) return;
    const enter = event.contentChanges.find(change => change.text.includes("\n"));
    if (!enter) return;

    const editor = vscode.window.visibleTextEditors.find(candidate => candidate.document === document);
    if (!editor) return;
    // `onDidChangeTextDocument` fires before some VSCodium builds update the
    // active editor selection. The edit range is authoritative: the line after
    // the final inserted newline is the freshly created comment line.
    const line = enter.range.start.line + (enter.text.match(/\n/g)?.length ?? 0);
    if (line === 0 || line >= document.lineCount) return;

    const previous = document.lineAt(line - 1).text;
    const current = document.lineAt(line).text;
    const documentation = previous.match(/^(\s*)\/\/\/.*/);
    if (!documentation || current.trimStart().startsWith("///")) return;

    const currentIndentation = current.match(/^\s*/)?.[0] ?? "";
    const prefix = `${documentation[1]}/// `;
    documentContinuationInProgress.add(document.uri.toString());
    try {
        const applied = await editor.edit(edit => edit.replace(
            new vscode.Range(line, 0, line, currentIndentation.length), prefix));
        if (applied) {
            const position = new vscode.Position(line, prefix.length);
            editor.selection = new vscode.Selection(position, position);
        }
    } finally {
        documentContinuationInProgress.delete(document.uri.toString());
    }
}

async function triggerDocumentationTemplateSuggestion(event: vscode.TextDocumentChangeEvent): Promise<void> {
    if (event.document.languageId !== "sushi" || !event.contentChanges.some(change => change.text === "/")) return;
    // Comment completions are disabled by default in VSCodium's quick-suggest
    // settings. Once the third slash has made a valid doc-comment marker, ask
    // the editor to show suggestions explicitly; accepting remains the user's
    // choice.
    await new Promise<void>(resolve => setTimeout(resolve, 0));
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document !== event.document) return;
    const position = editor.selection.active;
    const line = event.document.lineAt(position.line).text;
    if (!/^\s*\/\/\/\s*$/.test(line) || position.character !== line.length) return;
    if (position.line + 1 >= event.document.lineCount || event.document.lineAt(position.line + 1).text.trim().length === 0) return;
    await vscode.commands.executeCommand("editor.action.triggerSuggest");
}

export async function deactivate(): Promise<void> { await client?.stop(); }

async function startLanguageClient(): Promise<void> {
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
    await client.start();
}

async function restartLanguageClient(): Promise<void> {
    await client?.stop();
    client = undefined;
    await startLanguageClient();
}

function activeSushiDocument(uri?: vscode.Uri): vscode.TextDocument {
    const document = uri ? vscode.workspace.textDocuments.find(candidate => candidate.uri.toString() === uri.toString()) : vscode.window.activeTextEditor?.document;
    if (!document || document.languageId !== "sushi") throw new Error("Open a Sushi source document first.");
    return document;
}

async function transpile(document: vscode.TextDocument): Promise<TranspileResult> {
    if (!client) throw new Error("Sushi Language Server is not running.");
    const targetProfile = vscode.workspace.getConfiguration("sushi", document.uri).get<string>("targetProfile", "auto");
    return client.sendRequest<TranspileResult>("sushi/transpileDocument", {
        textDocument: { uri: document.uri.toString() }, text: document.getText(), targetProfile
    });
}

function shellQuote(value: string): string { return `'${value.replace(/'/g, "'\\''")}'`; }

function runCommand(script: string, directory: string, targetProfile: string): string {
    const quotedScript = shellQuote(script);
    const quotedDirectory = shellQuote(directory);
    if (targetProfile.startsWith("powershell")) {
        const psScript = script.replace(/'/g, "''");
        const psDirectory = directory.replace(/'/g, "''");
        return `& '${psScript}'; $code = $LASTEXITCODE; Remove-Item -LiteralPath '${psDirectory}' -Recurse -Force; exit $code`;
    }
    const shell = targetProfile.startsWith("zsh") ? "zsh" : "bash";
    return `${shell} ${quotedScript}; code=$?; rm -rf ${quotedDirectory}; exit $code`;
}
