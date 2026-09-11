namespace Sushi.Transpilation.Lowering;

using Sushi.Application;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class AstToIrLowerer
{
    private const string UnsupportedSyntaxCode = "SUSHI1001";
    private const string UndefinedIdentifierCode = "SUSHI1002";
    private const string UnsupportedCallCode = "SUSHI1003";
    private const string InvalidAssignmentTargetCode = "SUSHI1004";
    private const string UnresolvedNamedCallCode = "SUSHI1017";
    private const string UnsupportedTypeCode = "SUSHI1020";
    private const string ParameterTypeMismatchCode = "SUSHI1021";
    private const string StructuralFieldMissingCode = "SUSHI1022";
    private const string StructuralFieldTypeMismatchCode = "SUSHI1023";
    private const string ReturnTypeMismatchCode = "SUSHI1024";
    private const string InvalidStructuralDeclarationCode = "SUSHI1025";
    private const string UnsupportedStructuralConstructCode = "SUSHI1026";
    private const string InvalidBreakCode = "SUSHI1027";
    private const string InvalidContinueCode = "SUSHI1028";
    private const string InvalidReturnCode = "SUSHI1029";
    private static readonly Dictionary<string, string> StringMethodIntrinsicMap = new(StringComparer.Ordinal)
    {
        ["trim"] = "std.string.trim",
        ["lower"] = "std.string.lower",
        ["upper"] = "std.string.upper",
        ["split"] = "std.string.split",
        ["contains"] = "std.string.contains",
        ["startsWith"] = "std.string.startsWith",
        ["endsWith"] = "std.string.endsWith",
        ["replace"] = "std.string.replace",
        ["isMatch"] = "std.string.isMatch",
        ["match"] = "std.string.match"
    };

    private readonly List<Diagnostic> _diagnostics = new();
    private readonly IntrinsicRegistry _intrinsicRegistry = IntrinsicRegistry.CreateDefault();
    private readonly Dictionary<string, IrFunctionSignature> _functionSignatures = new();
    private readonly Dictionary<string, ClassDeclarationNode> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDeclarationNode> _enums = new(StringComparer.Ordinal);
    private readonly List<IrFunctionDeclarationStatement> _liftedFunctions = new();
    private readonly HashSet<string> _globalVariables = new(StringComparer.Ordinal);
    private readonly TargetProfile _targetProfile;
    private readonly string _symbolPrefix;
    private readonly IReadOnlyDictionary<string, string> _externalSymbols;
    private readonly IReadOnlySet<string> _moduleAliases;
    private readonly Dictionary<string, string> _topLevelSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knownObjectTypes = new(StringComparer.Ordinal);

    private string _sourcePath = "";
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _definedVariables = new(StringComparer.Ordinal);
    private bool _validateIdentifiers;
    private int _tempId;
    private int _lambdaId;
    private int _loopDepth;
    private int _functionDepth;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public AstToIrLowerer(
        TargetProfile? targetProfile = null,
        string symbolPrefix = "",
        IReadOnlyDictionary<string, string>? externalSymbols = null,
        IReadOnlySet<string>? moduleAliases = null)
    {
        _targetProfile = targetProfile ?? TargetProfile.Host();
        _symbolPrefix = symbolPrefix;
        _externalSymbols = externalSymbols ?? new Dictionary<string, string>();
        _moduleAliases = moduleAliases ?? new HashSet<string>();
    }

    public IrProgram Lower(ProgramNode program, string sourcePath)
    {
        _sourcePath = sourcePath;
        _diagnostics.Clear();
        _functionSignatures.Clear();
        _classes.Clear();
        _enums.Clear();
        _liftedFunctions.Clear();
        _globalVariables.Clear();
        _topLevelSymbols.Clear();
        _knownObjectTypes.Clear();
        _tempId = 0;
        _lambdaId = 0;
        _loopDepth = 0;
        _functionDepth = 0;
        CollectTopLevelSymbols(program);
        CollectTypes(program);
        CollectGlobalVariables(program);
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        _validateIdentifiers = false;
        CollectFunctionSignatures(program);
        _validateIdentifiers = true;

        var output = new IrProgram();

        foreach (var declaration in program.Declarations)
        {
            var lowered = LowerTopLevel(declaration);
            if (lowered != null)
            {
                if (declaration is ClassDeclarationNode or EnumDeclarationNode or ExportDeclarationNode { Declaration: ClassDeclarationNode or EnumDeclarationNode } && lowered is IrBlockStatement typeBlock)
                    output.Statements.AddRange(typeBlock.Statements);
                else
                    output.Statements.Add(lowered);
            }
        }

        foreach (var lifted in _liftedFunctions)
        {
            output.Statements.Insert(0, lifted);
        }

        return output;
    }

    private IrStatement? LowerTopLevel(AstNode node)
    {
        return node switch
        {
            BoxDeclarationNode => null,
            UseDeclarationNode => null,
            ExportDeclarationNode export => LowerTopLevel(export.Declaration),
            FunctionDeclarationNode function => LowerFunction(function),
            ClassDeclarationNode classDeclaration => LowerClass(classDeclaration),
            EnumDeclarationNode enumDeclaration => LowerEnum(enumDeclaration),
            StatementNode statement => LowerStatement(statement),
            _ => UnsupportedStatement(node, "Top-level declaration is not yet supported in transpilation")
        };
    }

    private IrFunctionDeclarationStatement? LowerFunction(FunctionDeclarationNode node)
    {
        var emittedName = ResolveTopLevel(node.Name);
        var signature = _functionSignatures.TryGetValue(emittedName, out var existingSignature)
            ? existingSignature
            : BuildFunctionSignature(node);

        var previousFunctionName = _currentFunctionName;
        var previousReturnType = _currentFunctionReturnType;
        var previousVariables = _definedVariables;
        var previousLoopDepth = _loopDepth;
        _currentFunctionName = emittedName;
        _currentFunctionReturnType = signature.ReturnType;
        _functionDepth++;
        _loopDepth = 0;
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        foreach (var parameter in signature.Parameters)
        {
            _definedVariables.Add(parameter.Name);
        }
        AddAnonymousStructuralFieldNames(node.Parameters, signature.Parameters);

        var body = node.Body switch
        {
            BlockStatementNode block => LowerBlock(block),
            StatementNode statement => LowerSingleStatementBlock(statement),
            _ => null
        };

        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;
        _functionDepth--;
        _loopDepth = previousLoopDepth;
        _definedVariables = previousVariables;

        if (body == null)
        {
            AddDiagnostic(UnsupportedSyntaxCode, "Function body could not be lowered", node.Line, node.Column);
            return null;
        }

        InjectAnonymousStructuralFieldBindings(node, signature, body);

        return new IrFunctionDeclarationStatement(emittedName, signature.Parameters, body, signature.ReturnType);
    }

    private IrBlockStatement LowerBlock(BlockStatementNode block)
    {
        var statements = new List<IrStatement>();

        foreach (var statement in block.Statements)
        {
            var lowered = LowerStatement(statement);
            if (lowered != null)
            {
                statements.Add(lowered);
            }
        }

        return new IrBlockStatement(statements);
    }

    private IrStatement? LowerStatement(StatementNode node)
    {
        switch (node)
        {
            case BlockStatementNode block:
                return LowerBlock(block);

            case VariableDeclarationStatementNode declaration:
            {
                if (declaration.Initializer is NewExpressionNode constructed)
                    _knownObjectTypes[declaration.Name] = ResolveCallable(constructed.TypeName);
                var initializer = declaration.Initializer != null ? LowerExpression(declaration.Initializer) : null;
                var declarationName = _functionDepth == 0 ? ResolveTopLevel(declaration.Name) : declaration.Name;
                _definedVariables.Add(declaration.Name);
                _definedVariables.Add(declarationName);
                return new IrVariableDeclarationStatement(declarationName, initializer);
            }

            case ExpressionStatementNode expressionStatement:
                return new IrExpressionStatement(LowerExpression(expressionStatement.Expression));

            case IfStatementNode ifStatement:
            {
                var condition = LowerExpression(ifStatement.Condition);
                if (condition is IrLiteralExpression { Value: bool value })
                {
                    return value
                        ? StatementToBlock(ifStatement.ThenBranch)
                        : ifStatement.ElseBranch != null ? StatementToBlock(ifStatement.ElseBranch) : new IrBlockStatement();
                }

                return new IrIfStatement(
                    condition,
                    StatementToBlock(ifStatement.ThenBranch),
                    ifStatement.ElseBranch != null ? StatementToBlock(ifStatement.ElseBranch) : null);
            }

            case WhileStatementNode whileStatement:
                return new IrWhileStatement(
                    LowerExpression(whileStatement.Condition),
                    LowerLoopBody(whileStatement.Body));

            case ForStatementNode forStatement:
                return new IrForStatement(
                    forStatement.Initializer != null ? LowerStatement(forStatement.Initializer) : null,
                    forStatement.Condition != null ? LowerExpression(forStatement.Condition) : null,
                    forStatement.Increment != null ? LowerExpression(forStatement.Increment) : null,
                    LowerLoopBody(forStatement.Body));

            case DoWhileStatementNode doWhile:
                return new IrDoWhileStatement(
                    LowerLoopBody(doWhile.Body),
                    LowerExpression(doWhile.Condition));

            case ForRangeStatementNode forRange:
                return LowerForRangeStatement(forRange);

            case ForEachStatementNode forEach:
                return LowerForEachStatement(forEach);

            case ArrayDestructuringStatementNode destructuring:
                return LowerArrayDestructuringStatement(destructuring);

            case SwitchStatementNode switchStatement:
                return LowerSwitchStatement(switchStatement);

            case ReturnStatementNode returnStatement:
            {
                if (_functionDepth == 0)
                {
                    AddDiagnostic(
                        InvalidReturnCode,
                        "return can only be used inside a function or lambda.",
                        returnStatement.Line,
                        returnStatement.Column);
                    return null;
                }

                var expression = returnStatement.Expression != null ? LowerExpression(returnStatement.Expression) : null;
                ValidateReturnType(expression, returnStatement.Line, returnStatement.Column);
                return new IrReturnStatement(expression);
            }

            case BreakStatementNode breakStatement:
                if (_loopDepth == 0)
                {
                    AddDiagnostic(
                        InvalidBreakCode,
                        "break can only be used inside a loop.",
                        breakStatement.Line,
                        breakStatement.Column);
                    return null;
                }
                return new IrBreakStatement();

            case ContinueStatementNode continueStatement:
                if (_loopDepth == 0)
                {
                    AddDiagnostic(
                        InvalidContinueCode,
                        "continue can only be used inside a loop.",
                        continueStatement.Line,
                        continueStatement.Column);
                    return null;
                }
                return new IrContinueStatement();

            default:
                return UnsupportedStatement(node, "Statement is not yet supported in Milestone 1 transpilation");
        }
    }

    private IrBlockStatement StatementToBlock(StatementNode statement)
    {
        if (statement is BlockStatementNode block)
        {
            return LowerBlock(block);
        }

        return LowerSingleStatementBlock(statement);
    }

    private IrBlockStatement LowerSingleStatementBlock(StatementNode statement)
    {
        var lowered = LowerStatement(statement);
        if (lowered == null)
        {
            return new IrBlockStatement();
        }

        return new IrBlockStatement(new[] { lowered });
    }

    private IrBlockStatement LowerLoopBody(StatementNode statement)
    {
        _loopDepth++;
        try
        {
            return StatementToBlock(statement);
        }
        finally
        {
            _loopDepth--;
        }
    }

    private IrStatement LowerForRangeStatement(ForRangeStatementNode node)
    {
        var startExpression = LowerExpression(node.Start);
        var endExpression = LowerExpression(node.End);
        var stepExpression = node.Step != null
            ? LowerExpression(node.Step)
            : new IrLiteralExpression(1);
        _definedVariables.Add(node.Variable);
        var iterator = new IrIdentifierExpression(node.Variable);
        var comparisonOperator = node.IsInclusive ? "<=" : "<";

        return new IrForStatement(
            new IrVariableDeclarationStatement(node.Variable, startExpression),
            new IrBinaryExpression(
                iterator,
                comparisonOperator,
                endExpression),
            new IrAssignmentExpression(
                iterator,
                "+=",
                stepExpression),
            LowerLoopBody(node.Body));
    }

    private IrStatement LowerForEachStatement(ForEachStatementNode node)
    {
        var collectionExpression = LowerExpression(node.Collection);
        _definedVariables.Add(node.ItemVariable);
        if (!string.IsNullOrWhiteSpace(node.IndexVariable))
        {
            _definedVariables.Add(node.IndexVariable);
        }

        var collectionTemp = CreateTempName("each_collection");
        var indexTemp = CreateTempName("each_index");
        var itemValue = new IrIndexExpression(
            new IrIdentifierExpression(collectionTemp),
            new IrIdentifierExpression(indexTemp));
        var lengthCall = new IrCallExpression(
            "__sushi_json_length",
            new IrExpression[] { new IrIdentifierExpression(collectionTemp) });

        var loopBodyStatements = new List<IrStatement>();
        if (node.IndexVariable != null)
        {
            loopBodyStatements.Add(
                new IrVariableDeclarationStatement(node.IndexVariable, new IrIdentifierExpression(indexTemp)));
        }

        loopBodyStatements.Add(
            new IrVariableDeclarationStatement(node.ItemVariable, itemValue));

        loopBodyStatements.AddRange(LowerLoopBody(node.Body).Statements);

        return new IrBlockStatement(new IrStatement[]
        {
            new IrVariableDeclarationStatement(collectionTemp, collectionExpression),
            new IrVariableDeclarationStatement(indexTemp, new IrLiteralExpression(0)),
            new IrWhileStatement(
                new IrBinaryExpression(
                    new IrIdentifierExpression(indexTemp),
                    "<",
                    lengthCall),
                new IrBlockStatement(loopBodyStatements.Append<IrStatement>(
                    new IrExpressionStatement(
                        new IrAssignmentExpression(
                            new IrIdentifierExpression(indexTemp),
                            "+=",
                            new IrLiteralExpression(1))))))
        });
    }

    private IrStatement LowerArrayDestructuringStatement(ArrayDestructuringStatementNode node)
    {
        var valueTemp = CreateTempName("destructure_value");
        var statements = new List<IrStatement>
        {
            new IrVariableDeclarationStatement(valueTemp, LowerExpression(node.Value))
        };

        LowerDestructuringPatterns(
            node.Patterns,
            new IrIdentifierExpression(valueTemp),
            statements,
            0);

        return new IrBlockStatement(statements);
    }

    private int LowerDestructuringPatterns(
        IReadOnlyList<DestructuringPatternNode> patterns,
        IrExpression source,
        List<IrStatement> output,
        int startIndex)
    {
        var index = startIndex;
        foreach (var pattern in patterns)
        {
            if (pattern.IsRest && pattern.Name != null)
            {
                _definedVariables.Add(pattern.Name);
                output.Add(new IrVariableDeclarationStatement(
                    pattern.Name,
                    new IrCallExpression(
                        "__sushi_slice",
                        new IrExpression[]
                        {
                            source,
                            new IrLiteralExpression(index),
                            new IrLiteralExpression(null)
                        })));
                continue;
            }

            var currentValue = (IrExpression)new IrIndexExpression(source, new IrLiteralExpression(index));
            if (pattern.DefaultValue != null)
            {
                currentValue = new IrConditionalExpression(
                    new IrBinaryExpression(
                        currentValue,
                        "==",
                        new IrLiteralExpression("")),
                    LowerExpression(pattern.DefaultValue),
                    currentValue);
            }

            if (pattern.NestedPatterns != null && pattern.NestedPatterns.Count > 0)
            {
                var nestedTemp = CreateTempName("destructure_nested");
                output.Add(new IrVariableDeclarationStatement(nestedTemp, currentValue));
                _ = LowerDestructuringPatterns(
                    pattern.NestedPatterns,
                    new IrIdentifierExpression(nestedTemp),
                    output,
                    0);
            }
            else if (pattern.Name != null)
            {
                _definedVariables.Add(pattern.Name);
                output.Add(new IrVariableDeclarationStatement(pattern.Name, currentValue));
            }

            index++;
        }

        return index;
    }

    private IrStatement LowerSwitchStatement(SwitchStatementNode node)
    {
        var switchTemp = CreateTempName("switch_value");
        var loweredCases = node.Cases
            .Select(c => (Case: c, Match: LowerCaseMatchExpression(new IrIdentifierExpression(switchTemp), c)))
            .ToList();

        IrBlockStatement? nextElse = node.DefaultCase != null ? LowerBlock(node.DefaultCase) : null;
        for (var i = loweredCases.Count - 1; i >= 0; i--)
        {
            var current = loweredCases[i];
            nextElse = new IrBlockStatement(new IrStatement[]
            {
                new IrIfStatement(
                    current.Match,
                    LowerBlock(current.Case.Body),
                    nextElse)
            });
        }

        var statements = new List<IrStatement>
        {
            new IrVariableDeclarationStatement(switchTemp, LowerExpression(node.Value))
        };

        if (nextElse != null)
        {
            statements.AddRange(nextElse.Statements);
        }

        return new IrBlockStatement(statements);
    }

    private IrExpression LowerCaseMatchExpression(IrExpression switchValue, SwitchCaseNode @case)
    {
        IrExpression? result = null;
        foreach (var match in @case.MatchValues)
        {
            var equals = new IrBinaryExpression(switchValue, "==", LowerExpression(match));
            result = result == null ? equals : new IrBinaryExpression(result, "||", equals);
        }

        return result ?? new IrLiteralExpression(false);
    }

    private string CreateTempName(string prefix)
    {
        _tempId++;
        return $"__sushi_{prefix}_{_tempId}";
    }

    private IrExpression LowerExpression(ExpressionNode node)
    {
        return node switch
        {
            LiteralExpressionNode literal => new IrLiteralExpression(literal.Value),
            IdentifierExpressionNode identifier => LowerIdentifier(identifier),
            ThisExpressionNode => new IrIdentifierExpression("this"),
            ParenthesizedExpressionNode parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionNode unary => new IrUnaryExpression(unary.Operator, LowerExpression(unary.Operand), unary.IsPrefix),
            BinaryExpressionNode binary => LowerBinary(binary),
            ConditionalExpressionNode conditional => new IrConditionalExpression(
                LowerExpression(conditional.Condition),
                LowerExpression(conditional.TrueExpression),
                LowerExpression(conditional.FalseExpression)),
            ArrayLiteralExpressionNode array => new IrArrayLiteralExpression(array.Elements.Select(LowerExpression)),
            ObjectLiteralExpressionNode obj => LowerObjectLiteral(obj),
            InterpolatedStringExpressionNode interpolated => LowerInterpolatedString(interpolated),
            MemberAccessExpressionNode member => LowerMemberAccess(member),
            IndexExpressionNode index => new IrIndexExpression(LowerExpression(index.Array), LowerExpression(index.Index)),
            SliceExpressionNode slice => new IrCallExpression(
                "__sushi_slice",
                new IrExpression[]
                {
                    LowerExpression(slice.Array),
                    slice.Start != null ? LowerExpression(slice.Start) : new IrLiteralExpression(null),
                    slice.End != null ? LowerExpression(slice.End) : new IrLiteralExpression(null)
                }),
            PipeExpressionNode pipe => LowerPipeExpression(pipe),
            CallExpressionNode call => LowerCall(call),
            NewExpressionNode @new => LowerNewExpression(@new),
            LambdaExpressionNode lambda => LowerLambdaExpression(lambda),
            _ => UnsupportedExpression(node)
        };
    }

    private IrExpression LowerMemberAccess(MemberAccessExpressionNode member)
    {
        if (TryGetCalleePath(member, out var path) && _externalSymbols.TryGetValue(path, out var external))
            return new IrIdentifierExpression(external);
        return new IrMemberAccessExpression(LowerExpression(member.Object), member.MemberName);
    }

    private IrExpression LowerInterpolatedString(InterpolatedStringExpressionNode node)
    {
        IrExpression result = new IrLiteralExpression("");
        foreach (var part in node.Parts)
        {
            var value = part.IsLiteral
                ? new IrLiteralExpression(part.Content)
                : LowerInterpolationExpression(part.Content, node);
            result = new IrBinaryExpression(result, "+", value);
        }

        return result;
    }

    private IrExpression LowerInterpolationExpression(string source, InterpolatedStringExpressionNode owner)
    {
        try
        {
            // The parser intentionally exposes programs, not standalone expressions.
            // A synthetic declaration gives interpolation the exact same grammar and
            // name/type validation as a regular expression.
            var tokens = new Tokenizer("var __sushi_interpolation = " + source).Tokenize().ToList();
            var parsed = new Parser(new Lexer(tokens).Lex().ToList()).Parse();
            if (parsed.Declarations.FirstOrDefault() is VariableDeclarationStatementNode declaration && declaration.Initializer != null)
            {
                return LowerExpression(declaration.Initializer);
            }
        }
        catch (Exception ex)
        {
            AddDiagnostic(UnsupportedSyntaxCode, $"Invalid string interpolation expression: {ex.Message}", owner.Line, owner.Column);
            return new IrLiteralExpression("");
        }

        AddDiagnostic(UnsupportedSyntaxCode, "Invalid string interpolation expression", owner.Line, owner.Column);
        return new IrLiteralExpression("");
    }

    private IrExpression LowerPipeExpression(PipeExpressionNode node)
    {
        // A pipe is syntax sugar only: value | fn(a, @, b) becomes fn(a, value, b).
        // Keeping this rewrite in the AST phase means normal intrinsic/function binding
        // and the existing native emitters handle the result without a pipeline runtime.
        if (node.Target is CallExpressionNode targetCall)
        {
            var arguments = targetCall.Arguments.ToList();
            var insertionIndex = node.PlaceholderIndex ?? 0;
            var sourceArgument = new ArgumentNode(null, node.Source, node.Source.Line, node.Source.Column);
            if (node.PlaceholderIndex.HasValue)
            {
                arguments[insertionIndex] = sourceArgument;
            }
            else
            {
                arguments.Insert(insertionIndex, sourceArgument);
            }

            return LowerCall(new CallExpressionNode(targetCall.Callee, arguments, node.Line, node.Column));
        }

        return LowerCall(new CallExpressionNode(
            node.Target,
            new List<ArgumentNode> { new(null, node.Source, node.Source.Line, node.Source.Column) },
            node.Line,
            node.Column));
    }

    private IrExpression LowerObjectLiteral(ObjectLiteralExpressionNode node)
    {
        var properties = node.Properties
            .Select(property => new IrObjectProperty(property.Name, LowerExpression(property.Value)))
            .ToList();

        return new IrObjectLiteralExpression(properties);
    }

    private IrExpression LowerBinary(BinaryExpressionNode node)
    {
        if (node.Operator is "=" or "+=" or "-=" or "*=" or "/=")
        {
            if (node.Left is MemberAccessExpressionNode member)
            {
                var target = LowerExpression(member.Object);
                var value = LowerExpression(node.Right);
                return new IrMemberAssignmentExpression(target, member.MemberName, node.Operator, value);
            }

            if (node.Left is not IdentifierExpressionNode identifier)
            {
                AddDiagnostic(
                    InvalidAssignmentTargetCode,
                    $"Assignment target for operator '{node.Operator}' must be an identifier",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }

            ValidateIdentifier(identifier);

            return new IrAssignmentExpression(
                new IrIdentifierExpression(identifier.Name),
                node.Operator,
                LowerExpression(node.Right));
        }

        var left = LowerExpression(node.Left);
        var right = LowerExpression(node.Right);
        if (left is IrLiteralExpression leftLiteral && right is IrLiteralExpression rightLiteral)
        {
            if (node.Operator is "==" or "===")
                return new IrLiteralExpression(Equals(leftLiteral.Value, rightLiteral.Value));
            if (node.Operator is "!=" or "!==")
                return new IrLiteralExpression(!Equals(leftLiteral.Value, rightLiteral.Value));
        }

        return new IrBinaryExpression(left, node.Operator, right);
    }

    private IrExpression LowerCall(CallExpressionNode node)
    {
        var loweredArguments = node.Arguments
            .Select(argument => new IrCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
                argument.Line,
                argument.Column))
            .ToList();

        var intrinsicArguments = loweredArguments
            .Select(argument => new IntrinsicCallArgument(
                argument.Name,
                argument.Value,
                argument.Line,
                argument.Column))
            .ToList();

        if (TryGetCalleePath(node.Callee, out var calleePath))
        {
            if (_intrinsicRegistry.TryResolve(calleePath, out var signature))
            {
                var binding = IntrinsicCallBinder.Bind(
                    signature,
                    intrinsicArguments,
                    _sourcePath,
                    node.Line,
                    node.Column);

                foreach (var diagnostic in binding.Diagnostics)
                {
                    _diagnostics.Add(diagnostic);
                }

                if (signature.DeprecationMessage != null)
                {
                    _diagnostics.Add(Diagnostic.Warning("SUSHI2001", signature.DeprecationMessage,
                        new SourceSpan(_sourcePath, node.Line, node.Column)));
                }

                if (!binding.Success)
                {
                    return new IrLiteralExpression(null);
                }

                if (signature.Id == IntrinsicId.TargetShell)
                    return new IrLiteralExpression(_targetProfile.ShellName);
                if (signature.Id == IntrinsicId.TargetPlatform)
                    return new IrLiteralExpression(_targetProfile.PlatformName);

                return new IrIntrinsicCallExpression(
                    signature.CanonicalName,
                    signature.Id,
                    binding.OrderedArguments);
            }

            if (_externalSymbols.TryGetValue(calleePath, out var externalCallee))
            {
                if (loweredArguments.Any(argument => argument.Name != null))
                {
                    AddDiagnostic(UnresolvedNamedCallCode,
                        $"Named arguments are not yet supported across module boundaries for '{calleePath}'.",
                        node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
                return new IrCallExpression(externalCallee, loweredArguments);
            }

            if (IsModuleQualified(calleePath))
            {
                AddDiagnostic("SUSHI1043", $"Module member '{calleePath}' is not exported.", node.Line, node.Column);
                return new IrLiteralExpression(null);
            }

            calleePath = ResolveCallable(calleePath);

            if (calleePath.StartsWith("std.", StringComparison.Ordinal))
            {
                AddDiagnostic(
                    IntrinsicDiagnosticCodes.UnknownIntrinsic,
                    $"Unknown intrinsic '{calleePath}'",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }
        }

        if (node.Callee is MemberAccessExpressionNode memberCallee)
        {
            if (memberCallee.Object is IdentifierExpressionNode objectIdentifier &&
                _knownObjectTypes.TryGetValue(objectIdentifier.Name, out var objectType))
            {
                var arguments = new List<IrCallArgument>
                {
                    new(null, new IrIdentifierExpression(ResolveCallable(objectIdentifier.Name)), memberCallee.Line, memberCallee.Column)
                };
                arguments.AddRange(loweredArguments);
                return new IrCallExpression($"__sushi_method_{objectType}_{memberCallee.MemberName}", arguments);
            }

            if (StringMethodIntrinsicMap.TryGetValue(memberCallee.MemberName, out var canonicalStringIntrinsic) &&
                _intrinsicRegistry.TryResolve(canonicalStringIntrinsic, out var stringSignature))
            {
                var stringIntrinsicArguments = new List<IntrinsicCallArgument>
                {
                    new(null, LowerExpression(memberCallee.Object), memberCallee.Line, memberCallee.Column)
                };
                stringIntrinsicArguments.AddRange(intrinsicArguments);

                var binding = IntrinsicCallBinder.Bind(
                    stringSignature,
                    stringIntrinsicArguments,
                    _sourcePath,
                    node.Line,
                    node.Column);

                foreach (var diagnostic in binding.Diagnostics)
                {
                    _diagnostics.Add(diagnostic);
                }

                if (!binding.Success)
                {
                    return new IrLiteralExpression(null);
                }

                return new IrIntrinsicCallExpression(
                    stringSignature.CanonicalName,
                    stringSignature.Id,
                    binding.OrderedArguments);
            }

            if (loweredArguments.Any(argument => argument.Name != null))
            {
                AddDiagnostic(
                    UnresolvedNamedCallCode,
                    $"Named arguments are not supported for method call '{memberCallee.MemberName}'.",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }

            return new IrMethodCallExpression(
                LowerExpression(memberCallee.Object),
                memberCallee.MemberName,
                loweredArguments);
        }

        if (node.Callee is not IdentifierExpressionNode callee)
        {
            AddDiagnostic(
                UnsupportedCallCode,
                "Unsupported call target in transpilation",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        var resolvedCallee = ResolveCallable(callee.Name);
        if (_functionSignatures.TryGetValue(resolvedCallee, out var functionSignature))
        {
            var binding = FunctionCallBinder.Bind(
                callee.Name,
                functionSignature.Parameters,
                loweredArguments,
                _sourcePath,
                node.Line,
                node.Column);

            foreach (var diagnostic in binding.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }

            if (!binding.Success)
            {
                return new IrLiteralExpression(null);
            }

            ValidateCallTypes(callee.Name, functionSignature.Parameters, binding.OrderedArguments);
            return new IrCallExpression(resolvedCallee, binding.OrderedArguments);
        }

        if (loweredArguments.Any(argument => argument.Name != null))
        {
            AddDiagnostic(
                UnresolvedNamedCallCode,
                $"Named arguments require a known Sushi function signature. Could not resolve '{callee.Name}'.",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        return new IrCallExpression(resolvedCallee, loweredArguments);
    }

    private IrExpression LowerNewExpression(NewExpressionNode node)
    {
        var resolvedTypeName = ResolveCallable(node.TypeName);
        var constructorName = $"__sushi_new_{resolvedTypeName}";
        var loweredArguments = node.Arguments
            .Select(argument => new IrCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
                argument.Line,
                argument.Column))
            .ToList();

        if (_functionSignatures.TryGetValue(constructorName, out var signature))
        {
            var binding = FunctionCallBinder.Bind(
                constructorName,
                signature.Parameters,
                loweredArguments,
                _sourcePath,
                node.Line,
                node.Column);

            foreach (var diagnostic in binding.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }

            if (!binding.Success)
            {
                return new IrLiteralExpression(null);
            }

            ValidateCallTypes(constructorName, signature.Parameters, binding.OrderedArguments);
            return new IrCallExpression(constructorName, binding.OrderedArguments);
        }

        if (loweredArguments.Any(argument => argument.Name != null))
        {
            AddDiagnostic(
                UnresolvedNamedCallCode,
                $"Named constructor arguments require a known class declaration. Could not resolve '{node.TypeName}'.",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        return new IrCallExpression(constructorName, loweredArguments);
    }

    private IrExpression LowerLambdaExpression(LambdaExpressionNode node)
    {
        var lambdaName = $"__sushi_lambda_{++_lambdaId}";
        var parameters = node.Parameters
            .Select(parameter => new IrFunctionParameter(
                parameter.Name,
                parameter.IsVarargs,
                parameter.DefaultValue != null ? LowerExpression(parameter.DefaultValue) : null,
                LowerParameterType(parameter, lambdaName)))
            .ToList();

        var previousVariables = _definedVariables;
        var previousLoopDepth = _loopDepth;
        _definedVariables = new HashSet<string>(previousVariables, StringComparer.Ordinal);
        _functionDepth++;
        _loopDepth = 0;
        foreach (var parameter in parameters)
        {
            _definedVariables.Add(parameter.Name);
        }

        IrBlockStatement body;
        if (node.IsBlock)
        {
            body = node.Body is BlockStatementNode block
                ? LowerBlock(block)
                : new IrBlockStatement();
        }
        else if (node.Body is ExpressionNode expressionNode)
        {
            body = new IrBlockStatement(new IrStatement[]
            {
                new IrReturnStatement(LowerExpression(expressionNode))
            });
        }
        else
        {
            body = new IrBlockStatement();
        }

        _functionDepth--;
        _loopDepth = previousLoopDepth;
        _definedVariables = previousVariables;

        var lifted = new IrFunctionDeclarationStatement(
            lambdaName,
            parameters,
            body,
            IrTypeRef.Any);
        _liftedFunctions.Add(lifted);
        _functionSignatures[lambdaName] = new IrFunctionSignature(IrTypeRef.Any, parameters);

        return new IrLiteralExpression(lambdaName);
    }

    private void CollectFunctionSignatures(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            if (declaration is FunctionDeclarationNode function)
            {
                _functionSignatures[ResolveTopLevel(function.Name)] = BuildFunctionSignature(function);
            }
            else if (declaration is ExportDeclarationNode { Declaration: FunctionDeclarationNode exportedFunction })
            {
                _functionSignatures[ResolveTopLevel(exportedFunction.Name)] = BuildFunctionSignature(exportedFunction);
            }
        }

        foreach (var classDeclaration in _classes.Values)
        {
            var resolvedClassName = ResolveTopLevel(classDeclaration.Name);
            var ctorName = $"__sushi_new_{resolvedClassName}";
            var ctorParameters = classDeclaration.Constructor?.Parameters
                ?? classDeclaration.Fields
                    .Select(field => new ParameterNode(
                        field.Type,
                        structuralType: null,
                        field.Name,
                        isVarargs: false,
                        field.Initializer,
                        classDeclaration.Line,
                        classDeclaration.Column))
                    .ToList();

            _functionSignatures[ctorName] = new IrFunctionSignature(
                IrTypeRef.Primitive("object"),
                BuildParameterList(ctorParameters, ctorName));

            foreach (var method in classDeclaration.Methods)
            {
                var methodName = $"__sushi_method_{resolvedClassName}_{method.Name}";
                var methodParameters = new List<IrFunctionParameter>
                {
                    new("this", false, null, IrTypeRef.Primitive("object"))
                };
                methodParameters.AddRange(BuildParameterList(method.Parameters, methodName));
                _functionSignatures[methodName] = new IrFunctionSignature(IrTypeRef.Any, methodParameters);
            }
        }

        foreach (var enumDeclaration in _enums.Values)
        {
            var resolvedEnumName = ResolveTopLevel(enumDeclaration.Name);
            foreach (var method in enumDeclaration.Methods)
            {
                var methodName = $"__sushi_method_{resolvedEnumName}_{method.Name}";
                var methodParameters = new List<IrFunctionParameter>
                {
                    new("this", false, null, IrTypeRef.Primitive("object"))
                };
                methodParameters.AddRange(BuildParameterList(method.Parameters, methodName));
                _functionSignatures[methodName] = new IrFunctionSignature(IrTypeRef.Any, methodParameters);
            }
        }
    }

    private void CollectTypes(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            var effectiveDeclaration = declaration is ExportDeclarationNode export ? export.Declaration : declaration;
            switch (effectiveDeclaration)
            {
                case ClassDeclarationNode classDeclaration:
                    _classes[classDeclaration.Name] = classDeclaration;
                    break;
                case EnumDeclarationNode enumDeclaration:
                    _enums[enumDeclaration.Name] = enumDeclaration;
                    break;
            }
        }
    }

    private void CollectTopLevelSymbols(ProgramNode program)
    {
        foreach (var raw in program.Declarations)
        {
            var declaration = raw is ExportDeclarationNode export ? export.Declaration : raw;
            var name = declaration switch
            {
                FunctionDeclarationNode function => function.Name,
                ClassDeclarationNode @class => @class.Name,
                EnumDeclarationNode @enum => @enum.Name,
                VariableDeclarationStatementNode variable => variable.Name,
                _ => null
            };
            if (name != null) _topLevelSymbols[name] = _symbolPrefix + name;
        }
    }

    private string ResolveTopLevel(string name) =>
        _topLevelSymbols.TryGetValue(name, out var resolved) ? resolved : name;

    private string ResolveCallable(string name)
    {
        if (_externalSymbols.TryGetValue(name, out var external)) return external;
        return ResolveTopLevel(name);
    }

    private bool IsModuleQualified(string path)
    {
        var dot = path.IndexOf('.');
        return dot > 0 && _moduleAliases.Contains(path[..dot]);
    }

    private void CollectGlobalVariables(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            var effectiveDeclaration = declaration is ExportDeclarationNode export ? export.Declaration : declaration;
            switch (effectiveDeclaration)
            {
                case VariableDeclarationStatementNode variable:
                    _globalVariables.Add(variable.Name);
                    _globalVariables.Add(ResolveTopLevel(variable.Name));
                    break;
                case ArrayDestructuringStatementNode destructuring:
                    CollectPatternNames(destructuring.Patterns, _globalVariables);
                    break;
                case EnumDeclarationNode enumDeclaration:
                    _globalVariables.Add(enumDeclaration.Name);
                    _globalVariables.Add(ResolveTopLevel(enumDeclaration.Name));
                    break;
            }
        }
    }

    private static void CollectPatternNames(
        IEnumerable<DestructuringPatternNode> patterns,
        ISet<string> destination)
    {
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern.Name))
            {
                destination.Add(pattern.Name);
            }

            if (pattern.NestedPatterns != null)
            {
                CollectPatternNames(pattern.NestedPatterns, destination);
            }
        }
    }

    private void AddAnonymousStructuralFieldNames(
        IReadOnlyList<ParameterNode> sourceParameters,
        IReadOnlyList<IrFunctionParameter> loweredParameters)
    {
        var count = Math.Min(sourceParameters.Count, loweredParameters.Count);
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(sourceParameters[i].Name, "_", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var field in loweredParameters[i].DeclaredType.StructuralFields)
            {
                _definedVariables.Add(field.Name);
            }
        }
    }

    private IrExpression LowerIdentifier(IdentifierExpressionNode identifier)
    {
        ValidateIdentifier(identifier);
        return new IrIdentifierExpression(ResolveCallable(identifier.Name));
    }

    private void ValidateIdentifier(IdentifierExpressionNode identifier)
    {
        if (!_validateIdentifiers || _definedVariables.Contains(identifier.Name) || _topLevelSymbols.ContainsKey(identifier.Name) || _externalSymbols.ContainsKey(identifier.Name))
        {
            return;
        }

        AddDiagnostic(
            UndefinedIdentifierCode,
            $"Undefined identifier '{identifier.Name}'.",
            identifier.Line,
            identifier.Column);
    }

    private List<IrFunctionParameter> BuildParameterList(IEnumerable<ParameterNode> parameters, string functionName)
    {
        var parameterList = parameters.ToList();
        var output = new List<IrFunctionParameter>();
        for (var i = 0; i < parameterList.Count; i++)
        {
            var parameter = parameterList[i];
            if (parameter.IsVarargs && i != parameterList.Count - 1)
            {
                AddDiagnostic(
                    FunctionCallBinder.InvalidVarargsDeclarationCode,
                    $"Function '{functionName}' has an invalid varargs declaration. Varargs must be the final parameter.",
                    parameter.Line,
                    parameter.Column);
            }

            output.Add(new IrFunctionParameter(
                parameter.StructuralType != null && string.Equals(parameter.Name, "_", StringComparison.Ordinal)
                    ? $"__sushi_struct_param_{i}"
                    : parameter.Name,
                parameter.IsVarargs,
                parameter.DefaultValue != null ? LowerExpression(parameter.DefaultValue) : null,
                LowerParameterType(parameter, functionName)));
        }

        return output;
    }

    private IrStatement LowerClass(ClassDeclarationNode node)
    {
        var statements = new List<IrStatement>();
        var resolvedTypeName = ResolveTopLevel(node.Name);

        foreach (var method in node.Methods)
        {
            var methodName = $"__sushi_method_{resolvedTypeName}_{method.Name}";
            var parameters = new List<IrFunctionParameter>
            {
                new("this", false, null, IrTypeRef.Primitive("object"))
            };
            parameters.AddRange(BuildParameterList(method.Parameters, methodName));
            var previousVariables = _definedVariables;
            _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
            foreach (var parameter in parameters)
            {
                _definedVariables.Add(parameter.Name);
            }
            _functionDepth++;
            var body = method.Body is StatementNode statementBody
                ? StatementToBlock(statementBody)
                : new IrBlockStatement();
            _functionDepth--;
            _definedVariables = previousVariables;

            statements.Add(new IrFunctionDeclarationStatement(
                methodName,
                parameters,
                body,
                IrTypeRef.Any));
        }

        var constructorName = $"__sushi_new_{resolvedTypeName}";
        var ctorParameters = node.Constructor?.Parameters
            ?? node.Fields.Select(field => new ParameterNode(
                field.Type,
                structuralType: null,
                field.Name,
                isVarargs: false,
                field.Initializer,
                field.Line,
                field.Column)).ToList();

        var ctorSignature = _functionSignatures.TryGetValue(constructorName, out var signature)
            ? signature
            : new IrFunctionSignature(IrTypeRef.Primitive("object"), BuildParameterList(ctorParameters, constructorName));

        var previousConstructorVariables = _definedVariables;
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        foreach (var parameter in ctorSignature.Parameters)
        {
            _definedVariables.Add(parameter.Name);
        }

        var objectProperties = new List<IrObjectProperty>
        {
            new("__sushi_type", new IrLiteralExpression(node.Name))
        };

        foreach (var method in node.Methods)
        {
            objectProperties.Add(new IrObjectProperty(
                $"__sushi_method_{method.Name}",
                new IrLiteralExpression($"__sushi_method_{resolvedTypeName}_{method.Name}")));
        }

        foreach (var field in node.Fields)
        {
            var matchingCtorParameter = ctorSignature.Parameters.FirstOrDefault(p => p.Name == field.Name);
            if (matchingCtorParameter != null)
            {
                objectProperties.Add(new IrObjectProperty(field.Name, new IrIdentifierExpression(field.Name)));
            }
            else if (field.Initializer != null)
            {
                objectProperties.Add(new IrObjectProperty(field.Name, LowerExpression(field.Initializer)));
            }
            else
            {
                objectProperties.Add(new IrObjectProperty(field.Name, new IrLiteralExpression(null)));
            }
        }

        var ctorStatements = new List<IrStatement>
        {
            new IrVariableDeclarationStatement("this", new IrObjectLiteralExpression(objectProperties))
        };
        if (node.Constructor != null)
        {
            _definedVariables.Add("this");
            _functionDepth++;
            ctorStatements.AddRange(LowerBlock(node.Constructor.Body).Statements);
            _functionDepth--;
        }
        ctorStatements.Add(new IrReturnStatement(new IrIdentifierExpression("this")));
        var ctorBody = new IrBlockStatement(ctorStatements);

        statements.Add(new IrFunctionDeclarationStatement(
            constructorName,
            ctorSignature.Parameters,
            ctorBody,
            IrTypeRef.Primitive("object")));
        _definedVariables = previousConstructorVariables;

        return new IrBlockStatement(statements);
    }

    private IrStatement LowerEnum(EnumDeclarationNode node)
    {
        var statements = new List<IrStatement>();
        var resolvedTypeName = ResolveTopLevel(node.Name);
        var containerProperties = new List<IrObjectProperty>();
        var ordinal = 0;
        foreach (var value in node.Values)
        {
            var valueProperties = new List<IrObjectProperty>
            {
                new("__sushi_type", new IrLiteralExpression(node.Name)),
                new("__sushi_enum_name", new IrLiteralExpression(value.Name)),
                new("__sushi_enum_ordinal", new IrLiteralExpression(ordinal))
            };

            IrExpression enumValueExpression;
            if (value.DirectValue != null)
            {
                enumValueExpression = LowerExpression(value.DirectValue);
            }
            else
            {
                enumValueExpression = new IrLiteralExpression(ordinal);
            }

            valueProperties.Add(new IrObjectProperty("__sushi_enum_value", enumValueExpression));

            if (value.Properties != null)
            {
                foreach (var property in value.Properties)
                {
                    valueProperties.Add(new IrObjectProperty(property.Key, LowerExpression(property.Value)));
                }
            }

            if (node.RecordParameters != null &&
                value.ConstructorArgs != null)
            {
                for (var i = 0; i < node.RecordParameters.Count && i < value.ConstructorArgs.Count; i++)
                {
                    valueProperties.Add(new IrObjectProperty(
                        node.RecordParameters[i].Name,
                        LowerExpression(value.ConstructorArgs[i])));
                }
            }

            foreach (var method in node.Methods)
            {
                valueProperties.Add(new IrObjectProperty(
                    $"__sushi_method_{method.Name}",
                    new IrLiteralExpression($"__sushi_method_{resolvedTypeName}_{method.Name}")));
            }

            var valueObject = new IrObjectLiteralExpression(valueProperties);
            containerProperties.Add(new IrObjectProperty(value.Name, valueObject));
            ordinal++;
        }

        foreach (var method in node.Methods)
        {
            var methodName = $"__sushi_method_{resolvedTypeName}_{method.Name}";
            var parameters = new List<IrFunctionParameter>
            {
                new("this", false, null, IrTypeRef.Primitive("object"))
            };
            parameters.AddRange(BuildParameterList(method.Parameters, methodName));
            var previousVariables = _definedVariables;
            _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
            foreach (var parameter in parameters)
            {
                _definedVariables.Add(parameter.Name);
            }
            _functionDepth++;
            var body = method.Body is StatementNode statementBody
                ? StatementToBlock(statementBody)
                : new IrBlockStatement();
            _functionDepth--;
            _definedVariables = previousVariables;

            statements.Add(new IrFunctionDeclarationStatement(
                methodName,
                parameters,
                body,
                IrTypeRef.Any));
        }

        statements.Add(new IrVariableDeclarationStatement(
            resolvedTypeName,
            new IrObjectLiteralExpression(containerProperties)));

        return new IrBlockStatement(statements);
    }

    private IrFunctionSignature BuildFunctionSignature(FunctionDeclarationNode function)
    {
        var parameters = new List<IrFunctionParameter>();

        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var parameter = function.Parameters[i];
            if (parameter.IsVarargs && i != function.Parameters.Count - 1)
            {
                AddDiagnostic(
                    FunctionCallBinder.InvalidVarargsDeclarationCode,
                    $"Function '{function.Name}' has an invalid varargs declaration. Varargs must be the final parameter.",
                    parameter.Line,
                    parameter.Column);
            }

            var declaredType = LowerParameterType(parameter, function.Name);
            var parameterName = parameter.Name;
            if (parameter.StructuralType != null &&
                string.Equals(parameterName, "_", StringComparison.Ordinal))
            {
                parameterName = $"__sushi_struct_param_{i}";
            }

            parameters.Add(new IrFunctionParameter(
                parameterName,
                parameter.IsVarargs,
                parameter.DefaultValue != null ? LowerExpression(parameter.DefaultValue) : null,
                declaredType));
        }

        var returnType = LowerDeclaredType(
            function.ReturnType,
            function.Line,
            function.Column,
            $"return type for function '{function.Name}'");

        return new IrFunctionSignature(returnType, parameters);
    }

    private static void InjectAnonymousStructuralFieldBindings(
        FunctionDeclarationNode functionNode,
        IrFunctionSignature signature,
        IrBlockStatement body)
    {
        var inserts = new List<IrStatement>();
        var count = Math.Min(functionNode.Parameters.Count, signature.Parameters.Count);
        for (var i = 0; i < count; i++)
        {
            var sourceParameter = functionNode.Parameters[i];
            var loweredParameter = signature.Parameters[i];
            if (!string.Equals(sourceParameter.Name, "_", StringComparison.Ordinal))
            {
                continue;
            }

            if (loweredParameter.DeclaredType.Kind != IrTypeKind.Structural)
            {
                continue;
            }

            foreach (var field in loweredParameter.DeclaredType.StructuralFields)
            {
                inserts.Add(new IrVariableDeclarationStatement(
                    field.Name,
                    new IrMemberAccessExpression(
                        new IrIdentifierExpression(loweredParameter.Name),
                        field.Name)));
            }
        }

        if (inserts.Count == 0)
        {
            return;
        }

        body.Statements.InsertRange(0, inserts);
    }

    private IrTypeRef LowerParameterType(ParameterNode parameter, string functionName)
    {
        if (parameter.StructuralType == null)
        {
            return LowerDeclaredType(
                parameter.Type,
                parameter.Line,
                parameter.Column,
                $"parameter '{parameter.Name}' in function '{functionName}'");
        }

        return LowerStructuralType(
            parameter.StructuralType,
            parameter.Line,
            parameter.Column,
            $"parameter '{parameter.Name}' in function '{functionName}'");
    }

    private IrTypeRef LowerStructuralType(StructuralTypeNode structuralType, int line, int column, string context)
    {
        var fields = new List<IrStructuralField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in structuralType.Fields)
        {
            var fieldName = field.Key;
            if (!seen.Add(fieldName))
            {
                AddDiagnostic(
                    InvalidStructuralDeclarationCode,
                    $"Structural type in {context} contains duplicate field '{fieldName}'.",
                    line,
                    column);
                continue;
            }

            var rawType = field.Value;
            var optional = rawType.EndsWith("?", StringComparison.Ordinal);
            if (optional)
            {
                rawType = rawType[..^1];
            }

            if (string.IsNullOrWhiteSpace(rawType))
            {
                AddDiagnostic(
                    InvalidStructuralDeclarationCode,
                    $"Structural field '{fieldName}' in {context} has an invalid type declaration.",
                    line,
                    column);
                fields.Add(new IrStructuralField(fieldName, IrTypeRef.Unknown, optional));
                continue;
            }

            var fieldType = LowerDeclaredType(
                rawType,
                line,
                column,
                $"field '{fieldName}' in {context}");

            if (fieldType.Kind == IrTypeKind.Structural)
            {
                AddDiagnostic(
                    UnsupportedStructuralConstructCode,
                    $"Nested structural field types are not supported for field '{fieldName}' in {context}.",
                    line,
                    column);
                fieldType = IrTypeRef.Unknown;
            }

            fields.Add(new IrStructuralField(fieldName, fieldType, optional));
        }

        return IrTypeRef.Structural(fields);
    }

    private IrTypeRef LowerDeclaredType(string? declaredType, int line, int column, string context)
    {
        if (string.IsNullOrWhiteSpace(declaredType))
        {
            return IrTypeRef.Any;
        }

        var normalized = declaredType.Trim();
        if (normalized.EndsWith("?", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized.ToLowerInvariant() switch
        {
            "string" or "str" or "char" => IrTypeRef.Primitive("string"),
            "int" or "integer" or "long" or "short" => IrTypeRef.Primitive("int"),
            "float" or "double" or "decimal" or "number" => IrTypeRef.Primitive("float"),
            "bool" or "boolean" => IrTypeRef.Primitive("bool"),
            "array" or "list" => IrTypeRef.Primitive("array"),
            "object" or "map" or "dictionary" or "dict" => IrTypeRef.Primitive("object"),
            "any" or "var" => IrTypeRef.Any,
            _ => AddUnsupportedTypeAndReturnUnknown(normalized, line, column, context)
        };
    }

    private IrTypeRef AddUnsupportedTypeAndReturnUnknown(string typeName, int line, int column, string context)
    {
        AddDiagnostic(
            UnsupportedTypeCode,
            $"Unsupported type annotation '{typeName}' for {context}.",
            line,
            column);
        return IrTypeRef.Unknown;
    }

    private void ValidateCallTypes(
        string calleeName,
        IReadOnlyList<IrFunctionParameter> parameters,
        IReadOnlyList<IrCallArgument> orderedArguments)
    {
        var varargsIndex = parameters.ToList().FindIndex(parameter => parameter.IsVarargs);

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.DeclaredType.IsAnyOrUnknown)
            {
                continue;
            }

            if (parameter.IsVarargs)
            {
                for (var argumentIndex = Math.Max(i, 0); argumentIndex < orderedArguments.Count; argumentIndex++)
                {
                    var argument = orderedArguments[argumentIndex];
                    if (TryInferStaticType(argument.Value, out var actualVarargType) &&
                        actualVarargType.Kind == IrTypeKind.Primitive &&
                        string.Equals(actualVarargType.Name, "array", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ValidateExpressionAgainstType(
                        argument.Value,
                        parameter.DeclaredType,
                        argument.Line,
                        argument.Column,
                        $"argument for varargs parameter '{parameter.Name}' in function '{calleeName}'",
                        ParameterTypeMismatchCode);
                }

                continue;
            }

            if (i >= orderedArguments.Count)
            {
                continue;
            }

            var currentArgument = orderedArguments[i];
            ValidateExpressionAgainstType(
                currentArgument.Value,
                parameter.DeclaredType,
                currentArgument.Line,
                currentArgument.Column,
                $"argument '{parameter.Name}' in function '{calleeName}'",
                ParameterTypeMismatchCode);
        }

        if (varargsIndex < 0 && orderedArguments.Count > parameters.Count)
        {
            for (var i = parameters.Count; i < orderedArguments.Count; i++)
            {
                var argument = orderedArguments[i];
                AddDiagnostic(
                    ParameterTypeMismatchCode,
                    $"Unexpected argument at position {i + 1} for function '{calleeName}'.",
                    argument.Line,
                    argument.Column);
            }
        }
    }

    private void ValidateReturnType(IrExpression? expression, int line, int column)
    {
        if (_currentFunctionName == null || _currentFunctionReturnType.IsAnyOrUnknown)
        {
            return;
        }

        if (expression == null)
        {
            AddDiagnostic(
                ReturnTypeMismatchCode,
                $"Return value for function '{_currentFunctionName}' must match type '{DescribeType(_currentFunctionReturnType)}'.",
                line,
                column);
            return;
        }

        ValidateExpressionAgainstType(
            expression,
            _currentFunctionReturnType,
            line,
            column,
            $"return value of function '{_currentFunctionName}'",
            ReturnTypeMismatchCode);
    }

    private void ValidateExpressionAgainstType(
        IrExpression expression,
        IrTypeRef expectedType,
        int line,
        int column,
        string context,
        string mismatchCode)
    {
        if (expectedType.IsAnyOrUnknown)
        {
            return;
        }

        if (expectedType.Kind == IrTypeKind.Structural)
        {
            ValidateStructuralExpression(expression, expectedType, line, column, context, mismatchCode);
            return;
        }

        if (!TryInferStaticType(expression, out var actualType))
        {
            return;
        }

        if (!IsTypeAssignable(expectedType, actualType))
        {
            AddDiagnostic(
                mismatchCode,
                $"{context} expects type '{DescribeType(expectedType)}' but value has type '{DescribeType(actualType)}'.",
                line,
                column);
        }
    }

    private void ValidateStructuralExpression(
        IrExpression expression,
        IrTypeRef expectedType,
        int line,
        int column,
        string context,
        string mismatchCode)
    {
        if (expression is not IrObjectLiteralExpression objectLiteral)
        {
            if (TryInferStaticType(expression, out var actualType) &&
                !IsTypeAssignable(IrTypeRef.Primitive("object"), actualType))
            {
                AddDiagnostic(
                    mismatchCode,
                    $"{context} expects a structural object but value has type '{DescribeType(actualType)}'.",
                    line,
                    column);
            }

            return;
        }

        foreach (var field in expectedType.StructuralFields)
        {
            var property = objectLiteral.Properties.FirstOrDefault(candidate => candidate.Name == field.Name);
            if (property == null)
            {
                if (!field.Optional)
                {
                    AddDiagnostic(
                        StructuralFieldMissingCode,
                        $"{context} is missing required structural field '{field.Name}'.",
                        line,
                        column);
                }

                continue;
            }

            if (field.Type.Kind == IrTypeKind.Structural)
            {
                AddDiagnostic(
                    UnsupportedStructuralConstructCode,
                    $"Nested structural field validation is not supported for field '{field.Name}' in {context}.",
                    line,
                    column);
                continue;
            }

            if (field.Type.IsAnyOrUnknown)
            {
                continue;
            }

            if (!TryInferStaticType(property.Value, out var actualType))
            {
                continue;
            }

            if (!IsTypeAssignable(field.Type, actualType))
            {
                AddDiagnostic(
                    StructuralFieldTypeMismatchCode,
                    $"{context} field '{field.Name}' expects type '{DescribeType(field.Type)}' but got '{DescribeType(actualType)}'.",
                    line,
                    column);
            }
        }
    }

    private static bool TryInferStaticType(IrExpression expression, out IrTypeRef type)
    {
        switch (expression)
        {
            case IrLiteralExpression literal:
                type = InferLiteralType(literal.Value);
                return !type.IsAnyOrUnknown;

            case IrArrayLiteralExpression:
                type = IrTypeRef.Primitive("array");
                return true;

            case IrObjectLiteralExpression:
                type = IrTypeRef.Primitive("object");
                return true;

            case IrConditionalExpression conditional:
                if (TryInferStaticType(conditional.TrueExpression, out var trueType) &&
                    TryInferStaticType(conditional.FalseExpression, out var falseType) &&
                    IsTypeAssignable(trueType, falseType))
                {
                    type = trueType;
                    return true;
                }

                type = IrTypeRef.Unknown;
                return false;

            default:
                type = IrTypeRef.Unknown;
                return false;
        }
    }

    private static IrTypeRef InferLiteralType(object? value)
    {
        return value switch
        {
            null => IrTypeRef.Unknown,
            bool => IrTypeRef.Primitive("bool"),
            sbyte or byte or short or ushort or int or uint or long or ulong => IrTypeRef.Primitive("int"),
            float or double or decimal => IrTypeRef.Primitive("float"),
            char or string => IrTypeRef.Primitive("string"),
            _ => IrTypeRef.Unknown
        };
    }

    private static bool IsTypeAssignable(IrTypeRef expected, IrTypeRef actual)
    {
        if (expected.IsAnyOrUnknown || actual.IsAnyOrUnknown)
        {
            return true;
        }

        if (expected.Kind == IrTypeKind.Structural)
        {
            return actual.Kind == IrTypeKind.Primitive && string.Equals(actual.Name, "object", StringComparison.Ordinal);
        }

        if (expected.Kind != IrTypeKind.Primitive || actual.Kind != IrTypeKind.Primitive)
        {
            return false;
        }

        if (string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(expected.Name, "float", StringComparison.Ordinal) &&
            string.Equals(actual.Name, "int", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string DescribeType(IrTypeRef type)
    {
        if (type.Kind == IrTypeKind.Structural)
        {
            var fields = type.StructuralFields
                .Select(field => $"{field.Type.Name ?? "any"}{(field.Optional ? "?" : "")} {field.Name}");
            return $"object {{ {string.Join(", ", fields)} }}";
        }

        return type.Kind switch
        {
            IrTypeKind.Any => "any",
            IrTypeKind.Unknown => "unknown",
            _ => type.Name ?? "unknown"
        };
    }

    private static bool TryGetCalleePath(ExpressionNode callee, out string path)
    {
        switch (callee)
        {
            case IdentifierExpressionNode identifier:
                path = identifier.Name;
                return true;

            case MemberAccessExpressionNode memberAccess:
                if (!TryGetCalleePath(memberAccess.Object, out var objectPath))
                {
                    path = "";
                    return false;
                }

                path = $"{objectPath}.{memberAccess.MemberName}";
                return true;

            default:
                path = "";
                return false;
        }
    }

    private IrExpression UnsupportedExpression(AstNode node)
    {
        AddDiagnostic(
            UnsupportedSyntaxCode,
            $"Expression '{node.GetType().Name}' is not yet supported in Milestone 1 transpilation",
            node.Line,
            node.Column);

        return new IrLiteralExpression(null);
    }

    private IrStatement? UnsupportedStatement(AstNode node, string message)
    {
        AddDiagnostic(UnsupportedSyntaxCode, message, node.Line, node.Column);
        return null;
    }

    private void AddDiagnostic(string code, string message, int line, int column)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, new SourceSpan(_sourcePath, line, column)));
    }

    private sealed class IrFunctionSignature
    {
        public IrTypeRef ReturnType { get; }
        public List<IrFunctionParameter> Parameters { get; }

        public IrFunctionSignature(IrTypeRef returnType, List<IrFunctionParameter> parameters)
        {
            ReturnType = returnType;
            Parameters = parameters;
        }
    }
}
