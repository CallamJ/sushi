namespace Sushi.Application.Documentation;

using System.Text;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation;
using Sushi.Transpilation.Intrinsics;
using Sushi.Transpilation.Modules;

internal static class ApiDocumentationGenerator
{
    public static bool TryGenerate(string rootPath, string rootSource, bool includeBuiltins, out string markdown, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var collected = new List<Diagnostic>();
        var loader = new ModuleGraphLoader(collected);
        loader.LoadRoot(rootPath, rootSource);
        if (collected.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            markdown = "";
            diagnostics = collected;
            return false;
        }

        var output = new StringBuilder("# Sushi API Reference\n\n");
        foreach (var module in loader.OrderedModules)
            RenderModule(output, module);
        if (includeBuiltins) RenderBuiltins(output);
        markdown = output.ToString();
        diagnostics = collected;
        return true;
    }

    private static void RenderModule(StringBuilder output, LoadedModule module)
    {
        output.Append("## ").Append(module.BoxName ?? Path.GetFileNameWithoutExtension(module.SourcePath)).Append("\n\n");
        output.Append("Source: `").Append(Path.GetFileName(module.SourcePath)).Append("`\n\n");
        foreach (var export in module.Exports.Values)
            RenderDeclaration(output, export, module.SourceText);
    }

    private static void RenderDeclaration(StringBuilder output, AstNode declaration, string source)
    {
        var (kind, name, signature) = declaration switch
        {
            FunctionDeclarationNode function => ("Function", function.Name, Signature(function)),
            VariableDeclarationStatementNode variable => ("Variable", variable.Name, $"{variable.Type ?? "var"} {variable.Name}"),
            ClassDeclarationNode classDeclaration => ("Class", classDeclaration.Name, $"class {classDeclaration.Name}"),
            EnumDeclarationNode @enum => ("Enum", @enum.Name, $"enum {@enum.Name}"),
            _ => ("Declaration", "declaration", "declaration")
        };
        output.Append("### ").Append(name).Append("\n\n```sushi\n").Append(signature).Append("\n```\n\n");
        RenderComment(output, FindComment(source, name));
        if (declaration is ClassDeclarationNode @class)
        {
            foreach (var field in @class.Fields) RenderMember(output, field.Name, $"{field.Type ?? "var"} {field.Name}", source);
            if (@class.Constructor is { } constructor) RenderMember(output, "new", $"new({Parameters(constructor.Parameters)})", source);
            foreach (var method in @class.Methods) RenderMember(output, method.Name, Signature(method), source);
            foreach (var adapter in @class.TypeAdapters) RenderMember(output, adapter.TargetType, $"{adapter.TargetType}()", source);
        }
        else if (declaration is EnumDeclarationNode @enum)
        {
            foreach (var value in @enum.Values) RenderMember(output, value.Name, value.Name, source);
            foreach (var method in @enum.Methods) RenderMember(output, method.Name, Signature(method), source);
            foreach (var adapter in @enum.TypeAdapters) RenderMember(output, adapter.TargetType, $"{adapter.TargetType}()", source);
        }
    }

    private static void RenderMember(StringBuilder output, string name, string signature, string source)
    {
        output.Append("#### ").Append(name).Append("\n\n```sushi\n").Append(signature).Append("\n```\n\n");
        RenderComment(output, FindComment(source, name));
    }

    private static void RenderComment(StringBuilder output, DocumentationComment? comment)
    {
        if (comment is null) return;
        if (!String.IsNullOrWhiteSpace(comment.Body)) output.Append(RenderLinks(comment.Body)).Append("\n\n");
        var parameters = comment.TagsNamed("param").ToArray();
        if (parameters.Length > 0)
        {
            output.Append("| Parameter | Description |\n| --- | --- |\n");
            foreach (var parameter in parameters) output.Append("| `").Append(parameter.Subject).Append("` | ").Append(RenderLinks(parameter.Description)).Append(" |\n");
            output.Append('\n');
        }
        if (comment.ReturnsDocumentation is { Length: > 0 } returns) output.Append("**Returns:** ").Append(RenderLinks(returns)).Append("\n\n");
        foreach (var throws in comment.TagsNamed("throws")) output.Append("**Throws:** ").Append(RenderLinks(throws.Description)).Append("\n\n");
        if (comment.DeprecationMessage is { Length: > 0 } deprecated) output.Append("> **Deprecated:** ").Append(RenderLinks(deprecated)).Append("\n\n");
        foreach (var example in comment.TagsNamed("example")) output.Append("**Example**\n\n```sushi\n").Append(example.Description).Append("\n```\n\n");
    }

    private static void RenderBuiltins(StringBuilder output)
    {
        output.Append("## Built-ins\n\n");
        foreach (var function in StandardLibraryCatalog.CreateDefault().Functions.OrderBy(function => function.Name, StringComparer.Ordinal))
        {
            output.Append("### ").Append(function.Name).Append("\n\n```sushi\n")
                .Append(function.Name).Append('(').Append(string.Join(", ", function.Parameters.Select(parameter => parameter.DisplayName))).Append(") -> ").Append(function.ReturnType)
                .Append("\n```\n\n").Append(function.Documentation).Append("\n\n");
        }
    }

    private static DocumentationComment? FindComment(string source, string name)
    {
        var tokens = new Lexer(new Tokenizer(source).Tokenize()).Lex().Where(token => token.Kind != ClassifiedTokenKind.EndOfFile).ToArray();
        foreach (var comment in DocumentationParser.Parse(source))
        {
            var index = Array.FindIndex(tokens, token => token.Start == comment.TargetOffset);
            if (index < 0) continue;
            if (tokens[index].IsKeyword("export")) index++;
            if (index < tokens.Length && (tokens[index].IsKeyword("class") || tokens[index].IsKeyword("enum") || tokens[index].IsKeyword("var"))) index++;
            if (name == "new" && index < tokens.Length && tokens[index].IsKeyword("new")) return comment;
            if (index < tokens.Length && tokens[index].Kind == ClassifiedTokenKind.Identifier && tokens[index].Text == name) return comment;
            if (index + 1 < tokens.Length && tokens[index].Kind == ClassifiedTokenKind.Identifier && tokens[index + 1].Kind == ClassifiedTokenKind.Identifier && tokens[index + 1].Text == name) return comment;
        }
        return null;
    }

    private static string Signature(FunctionDeclarationNode function) => $"{function.ReturnType ?? "void"} {function.Name}({Parameters(function.Parameters)})";
    private static string Parameters(IEnumerable<ParameterNode> parameters) => string.Join(", ", parameters.Select(parameter => $"{parameter.Type ?? "var"}{(parameter.IsVarargs ? "..." : "")} {parameter.Name}"));
    private static string RenderLinks(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\\{@link\\s+([A-Za-z_][A-Za-z0-9_.]*)(?:\\s+([^}]+))?\\}", match => $"[{(match.Groups[2].Success ? match.Groups[2].Value.Trim() : match.Groups[1].Value)}](#{match.Groups[1].Value.ToLowerInvariant().Replace('.', '-')})");
}
