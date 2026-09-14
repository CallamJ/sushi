namespace Sushi.Build;

using System.Text.RegularExpressions;
using Sushi.Build.SyntaxTree;

/// <summary>Structured documentation preserved from consecutive <c>///</c> comments.</summary>
public sealed record DocumentationComment(
    int Start,
    int End,
    int Line,
    int Column,
    int TargetOffset,
    string Summary,
    string Body,
    IReadOnlyList<DocumentationTag> Tags)
{
    public IEnumerable<DocumentationTag> TagsNamed(string name) => Tags.Where(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public string? ParameterDocumentation(string name) => TagsNamed("param").FirstOrDefault(tag => tag.Subject == name)?.Description;
    public string? ReturnsDocumentation => TagsNamed("returns").FirstOrDefault()?.Description;
    public string? DeprecationMessage => TagsNamed("deprecated").FirstOrDefault()?.Description;
}

public sealed record DocumentationTag(string Name, string? Subject, string Description, int Start, int Line, int Column);
public sealed record DocumentationIssue(string Code, string Message, int Start, int Line, int Column);

/// <summary>Parses documentation independently from the compiler lexer, which removes comments.</summary>
public static class DocumentationParser
{
    private static readonly HashSet<string> KnownTags = ["param", "returns", "throws", "deprecated", "example"];
    private static readonly Regex TagPattern = new("^@(?<name>[A-Za-z][A-Za-z0-9_-]*)(?:\\s+(?<rest>.*))?$", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new("\\{@link\\s+(?<target>[A-Za-z_][A-Za-z0-9_.]*)(?:\\s+(?<label>[^}]+))?\\}", RegexOptions.Compiled);

    public static IReadOnlyList<DocumentationComment> Parse(string source)
    {
        var raw = new Tokenizer(source).Tokenize().ToArray();
        var comments = raw.Where(token => token.Kind == TokenKind.Comment && token.Text.StartsWith("///", StringComparison.Ordinal)).ToArray();
        var result = new List<DocumentationComment>();
        for (var index = 0; index < comments.Length;)
        {
            var first = comments[index];
            var group = new List<UnclassifiedToken> { first };
            index++;
            while (index < comments.Length && comments[index].Line == group[^1].Line + 1)
                group.Add(comments[index++]);

            var next = raw.FirstOrDefault(token => token.Start >= group[^1].End && token.Kind is not TokenKind.Whitespace and not TokenKind.Comment);
            if (next is null) continue;
            var lines = group.Select(comment => comment.Text[3..].TrimStart()).ToArray();
            var tags = new List<DocumentationTag>();
            var prose = new List<string>();
            foreach (var (text, line) in lines.Select((text, line) => (text, line)))
            {
                var match = TagPattern.Match(text);
                if (!match.Success) { prose.Add(text); continue; }
                var name = match.Groups["name"].Value;
                var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value.Trim() : "";
                string? subject = null;
                if (name.Equals("param", StringComparison.OrdinalIgnoreCase))
                {
                    var split = rest.IndexOfAny([' ', '\t']);
                    subject = split < 0 ? rest : rest[..split];
                    rest = split < 0 ? "" : rest[(split + 1)..].TrimStart();
                }
                tags.Add(new DocumentationTag(name, subject, rest, group[line].Start, group[line].Line, group[line].Column));
            }
            var body = string.Join("\n", prose).Trim();
            var summary = prose.FirstOrDefault(line => !String.IsNullOrWhiteSpace(line))?.Trim() ?? "";
            result.Add(new DocumentationComment(first.Start, group[^1].End, first.Line, first.Column, next.Start, summary, body, tags));
        }
        return result;
    }

    public static IEnumerable<DocumentationIssue> Validate(DocumentationComment comment, IEnumerable<string>? parameterNames = null, bool callable = true)
    {
        var validateParameters = parameterNames is not null;
        var parameters = parameterNames?.ToHashSet(StringComparer.Ordinal) ?? [];
        var seenParams = new HashSet<string>(StringComparer.Ordinal);
        var seenSingletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in comment.Tags)
        {
            if (!KnownTags.Contains(tag.Name))
                yield return Issue("SUSHI1101", $"Unknown documentation tag '@{tag.Name}'.", tag);
            if (tag.Name is "returns" or "deprecated" && !seenSingletons.Add(tag.Name))
                yield return Issue("SUSHI1102", $"Documentation tag '@{tag.Name}' may only appear once.", tag);
            if (tag.Name.Equals("param", StringComparison.OrdinalIgnoreCase))
            {
                if (validateParameters && (String.IsNullOrWhiteSpace(tag.Subject) || !parameters.Contains(tag.Subject)))
                    yield return Issue("SUSHI1103", $"'@param' does not name a parameter on this declaration.", tag);
                else if (!seenParams.Add(tag.Subject!))
                    yield return Issue("SUSHI1104", $"Parameter '{tag.Subject}' is documented more than once.", tag);
            }
            if (tag.Name.Equals("returns", StringComparison.OrdinalIgnoreCase) && !callable)
                yield return Issue("SUSHI1105", "'@returns' is only valid on a callable declaration.", tag);
            if (tag.Name.Equals("deprecated", StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(tag.Description))
                yield return Issue("SUSHI1106", "'@deprecated' requires a replacement or migration message.", tag);
        }
        if (validateParameters)
            foreach (var parameter in parameters.Where(parameter => !seenParams.Contains(parameter)))
                yield return new DocumentationIssue("SUSHI1107", $"Parameter '{parameter}' is not documented with '@param'.", comment.Start, comment.Line, comment.Column);
        foreach (Match link in Regex.Matches(comment.Body + "\n" + string.Join("\n", comment.Tags.Select(tag => tag.Description)), "\\{@link[^}]*\\}"))
            if (!LinkPattern.IsMatch(link.Value))
                yield return new DocumentationIssue("SUSHI1108", "Malformed '{@link ...}' documentation reference.", comment.Start + link.Index, comment.Line, comment.Column);
    }

    public static IEnumerable<DocumentationIssue> ValidateSource(string source)
    {
        var tokens = new Lexer(new Tokenizer(source).Tokenize()).Lex().Where(token => token.Kind != ClassifiedTokenKind.EndOfFile).ToArray();
        foreach (var comment in Parse(source))
        {
            var index = Array.FindIndex(tokens, token => token.Start == comment.TargetOffset);
            if (index < 0) { foreach (var issue in Validate(comment)) yield return issue; continue; }
            if (tokens[index].IsKeyword("export")) index++;
            var callable = false;
            IEnumerable<string>? parameters = null;
            if (index < tokens.Length && tokens[index].IsKeyword("new") && index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen)
            {
                callable = true;
                var end = FindMatching(tokens, index + 1);
                if (end > index) parameters = ParameterNames(tokens, index + 2, end);
            }
            if (index < tokens.Length && tokens[index].Kind == ClassifiedTokenKind.Identifier)
            {
                var nameIndex = index;
                if (index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.Identifier) nameIndex++;
                if (nameIndex + 1 < tokens.Length && tokens[nameIndex + 1].Kind == ClassifiedTokenKind.LeftParen)
                {
                    callable = true;
                    var end = FindMatching(tokens, nameIndex + 1);
                    if (end > nameIndex) parameters = ParameterNames(tokens, nameIndex + 2, end);
                }
            }
            foreach (var issue in Validate(comment, parameters, callable)) yield return issue;
        }
    }

    private static int FindMatching(IReadOnlyList<ClassifiedToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == ClassifiedTokenKind.LeftParen) depth++;
            else if (tokens[index].Kind == ClassifiedTokenKind.RightParen && --depth == 0) return index;
        }
        return -1;
    }

    private static IEnumerable<string> ParameterNames(IReadOnlyList<ClassifiedToken> tokens, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind != ClassifiedTokenKind.Identifier) continue;
            var previous = index > start ? tokens[index - 1] : null;
            // `end` is the closing parenthesis, which is needed to recognize
            // the final untyped parameter in a list such as `name(value)`.
            var next = index + 1 <= end ? tokens[index + 1] : null;
            var typedName = previous?.Kind == ClassifiedTokenKind.Identifier;
            var startsParameter = previous is null || previous.Kind == ClassifiedTokenKind.Comma;
            var untypedName = startsParameter && next?.Kind is
                ClassifiedTokenKind.Comma or ClassifiedTokenKind.RightParen or ClassifiedTokenKind.Operator;
            if (typedName || untypedName)
                yield return tokens[index].Text;
        }
    }

    public static IEnumerable<DocumentationLink> Links(DocumentationComment comment)
    {
        foreach (Match match in LinkPattern.Matches(comment.Body + "\n" + string.Join("\n", comment.Tags.Select(tag => tag.Description))))
            yield return new DocumentationLink(match.Groups["target"].Value, match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : null);
    }

    private static DocumentationIssue Issue(string code, string message, DocumentationTag tag) => new(code, message, tag.Start, tag.Line, tag.Column);
}

public sealed record DocumentationLink(string Target, string? Label);
