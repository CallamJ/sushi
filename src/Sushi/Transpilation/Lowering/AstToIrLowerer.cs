namespace Sushi.Transpilation.Lowering;

using System.Globalization;
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
    private const string FieldTypeMismatchCode = "SUSHI1048";
    private const string StructuralFieldMissingCode = "SUSHI1022";
    private const string StructuralFieldTypeMismatchCode = "SUSHI1023";
    private const string ReturnTypeMismatchCode = "SUSHI1024";
    private const string InvalidStructuralDeclarationCode = "SUSHI1025";
    private const string UnsupportedStructuralConstructCode = "SUSHI1026";
    private const string InvalidBreakCode = "SUSHI1027";
    private const string InvalidContinueCode = "SUSHI1028";
    private const string InvalidReturnCode = "SUSHI1029";
    private const string UnknownMemberCode = "SUSHI1031";
    private const string ImmutableValueCode = "SUSHI1032";
    private const string MissingAdapterCode = "SUSHI1033";
    private const string UnknownTypeCode = "SUSHI1034";
    private const string ReadOnlyExportCode = "SUSHI1045";
    private const string BooleanContextCode = "SUSHI1046";
    private const string UnknownTruthinessCode = "SUSHI1047";
    private const string InferredTypeConflictCode = "SUSHI1049";
    private const string VoidReturnValueCode = "SUSHI1051";
    private const string MissingReturnValueCode = "SUSHI1052";
    private static readonly Dictionary<string, string> StringMethodIntrinsicMap = new(StringComparer.Ordinal)
    {
        ["trim"] = "std.string.trim",
        ["lower"] = "std.string.lower",
        ["upper"] = "std.string.upper",
        ["length"] = "std.string.length",
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
    private readonly IReadOnlyDictionary<string, FunctionDeclarationNode> _externalFunctions;
    private readonly IReadOnlyDictionary<string, ClassDeclarationNode> _externalClasses;
    private readonly IReadOnlyDictionary<string, EnumDeclarationNode> _externalEnums;
    private readonly Dictionary<string, string> _topLevelSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knownObjectTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionObjectReturnTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrTypeRef> _knownVariableTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _standardImportAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _standardImportNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _standardImportedPaths = new(StringComparer.Ordinal);

    private string _sourcePath = "";
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _definedVariables = new(StringComparer.Ordinal);
    private bool _validateIdentifiers;
    private int _tempId;
    private int _lambdaId;
    private int _loopDepth;
    private int _functionDepth;
    private bool _allowEnumMutation;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public AstToIrLowerer(
        TargetProfile? targetProfile = null,
        string symbolPrefix = "",
        IReadOnlyDictionary<string, string>? externalSymbols = null,
        IReadOnlySet<string>? moduleAliases = null,
        IReadOnlyDictionary<string, FunctionDeclarationNode>? externalFunctions = null,
        IReadOnlyDictionary<string, ClassDeclarationNode>? externalClasses = null,
        IReadOnlyDictionary<string, EnumDeclarationNode>? externalEnums = null)
    {
        _targetProfile = targetProfile ?? TargetProfile.Host();
        _symbolPrefix = symbolPrefix;
        _externalSymbols = externalSymbols ?? new Dictionary<string, string>();
        _moduleAliases = moduleAliases ?? new HashSet<string>();
        _externalFunctions = externalFunctions ?? new Dictionary<string, FunctionDeclarationNode>();
        _externalClasses = externalClasses ?? new Dictionary<string, ClassDeclarationNode>();
        _externalEnums = externalEnums ?? new Dictionary<string, EnumDeclarationNode>();
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
        _functionObjectReturnTypes.Clear();
        _knownVariableTypes.Clear();
        _standardImportAliases.Clear();
        _standardImportNames.Clear();
        _standardImportedPaths.Clear();
        _tempId = 0;
        _lambdaId = 0;
        _loopDepth = 0;
        _functionDepth = 0;
        _allowEnumMutation = false;
        CollectStandardImports(program);
        CollectTopLevelSymbols(program);
        CollectTypes(program);
        ValidateTypeDeclarations(program);
        CollectGlobalVariables(program);
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        _validateIdentifiers = false;
        CollectFunctionSignatures(program);
        InferNamedFunctionReturnTypes(program);
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

    private void CollectStandardImports(ProgramNode program)
    {
        foreach (var use in program.Declarations.OfType<UseDeclarationNode>().Where(item => item.IsStandardLibrary))
        {
            var alias = use.Alias ?? use.ImportPath[(use.ImportPath.LastIndexOf('.') + 1)..];
            if (use.Members.Count == 0)
            {
                if (_intrinsicRegistry.TryResolve(use.ImportPath, out _))
                {
                    var member = use.ImportPath[(use.ImportPath.LastIndexOf('.') + 1)..];
                    if (!_standardImportNames.TryAdd(member, use.ImportPath))
                        AddDiagnostic("SUSHI1055", $"Duplicate standard-library import '{member}'.", use.Line, use.Column);
                    _standardImportedPaths.Add(use.ImportPath);
                    continue;
                }
                _standardImportedPaths.Add(use.ImportPath);
                if (!_standardImportAliases.TryAdd(alias, use.ImportPath))
                    AddDiagnostic("SUSHI1055", $"Duplicate standard-library alias '{alias}'.", use.Line, use.Column);
                continue;
            }

            foreach (var member in use.Members)
            {
                if (!_intrinsicRegistry.TryResolve($"{use.ImportPath}.{member}", out _))
                {
                    AddDiagnostic("SUSHI1056", $"Unknown standard-library member '{use.ImportPath}.{member}'.", use.Line, use.Column);
                    continue;
                }
                _standardImportedPaths.Add($"{use.ImportPath}.{member}");
                if (!_standardImportNames.TryAdd(member, $"{use.ImportPath}.{member}"))
                    AddDiagnostic("SUSHI1055", $"Duplicate standard-library import '{member}'.", use.Line, use.Column);
            }
        }
    }

    private string ResolveStandardImport(string path)
    {
        var dot = path.IndexOf('.');
        if (dot > 0 && _standardImportAliases.TryGetValue(path[..dot], out var module))
            return module + path[dot..];
        return _standardImportNames.TryGetValue(path, out var imported) ? imported : path;
    }

    private IrStatement? LowerTopLevel(AstNode node)
    {
        return node switch
        {
            BoxDeclarationNode => null,
            UseDeclarationNode use when use.IsStandardLibrary =>
                new IrStandardLibraryImportStatement(use.ImportPath, use.Alias, use.Members),
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
        var previousObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
        var previousVariableTypes = new Dictionary<string, IrTypeRef>(_knownVariableTypes, StringComparer.Ordinal);
        var previousLoopDepth = _loopDepth;
        _currentFunctionName = emittedName;
        _currentFunctionReturnType = signature.ReturnType;
        _functionDepth++;
        _loopDepth = 0;
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        foreach (var parameter in signature.Parameters)
        {
            _definedVariables.Add(parameter.Name);
            _knownVariableTypes[parameter.Name] = parameter.DeclaredType;
        }
        TrackParameterObjectTypes(node.Parameters, signature.Parameters);
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
        RestoreKnownObjectTypes(previousObjectTypes);
        RestoreKnownVariableTypes(previousVariableTypes);

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
                TrackVariableObjectType(declaration.Name, declaration.Type, declaration.Initializer);
                var initializer = declaration.Initializer != null ? LowerExpression(declaration.Initializer) : null;
                var declarationName = _functionDepth == 0 ? ResolveTopLevel(declaration.Name) : declaration.Name;
                DeclareVariableType(declaration.Name, declaration.Type, initializer, declaration.Line, declaration.Column);
                if (declarationName != declaration.Name)
                    _knownVariableTypes[declarationName] = _knownVariableTypes[declaration.Name];
                _definedVariables.Add(declaration.Name);
                _definedVariables.Add(declarationName);
                return new IrVariableDeclarationStatement(declarationName, initializer);
            }

            case ExpressionStatementNode expressionStatement:
                return new IrExpressionStatement(LowerExpression(expressionStatement.Expression));

            case IfStatementNode ifStatement:
            {
                var condition = LowerExpression(ifStatement.Condition);
                condition = RequireBooleanCondition(condition, ifStatement.Line, ifStatement.Column, "if condition");
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
            {
                var condition = LowerExpression(whileStatement.Condition);
                condition = RequireBooleanCondition(condition, whileStatement.Line, whileStatement.Column, "while condition");
                return new IrWhileStatement(
                    condition,
                    LowerLoopBody(whileStatement.Body));
            }

            case ForStatementNode forStatement:
            {
                var condition = forStatement.Condition != null ? LowerExpression(forStatement.Condition) : null;
                if (condition != null)
                    condition = RequireBooleanCondition(condition, forStatement.Line, forStatement.Column, "for condition");
                return new IrForStatement(
                    forStatement.Initializer != null ? LowerStatement(forStatement.Initializer) : null,
                    condition,
                    forStatement.Increment != null ? LowerExpression(forStatement.Increment) : null,
                    LowerLoopBody(forStatement.Body));
            }

            case DoWhileStatementNode doWhile:
            {
                var condition = LowerExpression(doWhile.Condition);
                condition = RequireBooleanCondition(condition, doWhile.Line, doWhile.Column, "do-while condition");
                return new IrDoWhileStatement(LowerLoopBody(doWhile.Body), condition);
            }

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
        var readablePrefix = prefix switch
        {
            "each_collection" => "each",
            "each_index" => "index",
            "destructure_value" => "value",
            "destructure_nested" => "nested",
            "switch_value" => "switch",
            _ => prefix
        };
        return $"_{readablePrefix}_{_tempId}";
    }

    private IrExpression LowerUnary(UnaryExpressionNode unary)
    {
        var operand = LowerExpression(unary.Operand);
        if (unary.Operator == "!")
            ValidateBooleanContext(operand, unary.Line, unary.Column, "operand of '!'");
        return new IrUnaryExpression(unary.Operator, operand, unary.IsPrefix);
    }

    private IrExpression LowerTruthiness(UnaryExpressionNode unary)
    {
        var operand = LowerExpression(unary.Operand);
        if (operand is IrLiteralExpression { Value: null })
            return new IrTruthinessExpression(operand, IrTypeRef.Primitive("null"));
        if (!TryInferStaticType(operand, out var type) || type.IsAnyOrUnknown)
        {
            AddDiagnostic(
                UnknownTruthinessCode,
                "Truthiness requires a statically known type; add a type annotation.",
                unary.Line,
                unary.Column);
            return new IrTruthinessExpression(operand, IrTypeRef.Unknown);
        }
        return new IrTruthinessExpression(operand, type);
    }

    private IrExpression LowerConditionalCondition(ConditionalExpressionNode conditional)
    {
        var condition = LowerExpression(conditional.Condition);
        return RequireBooleanCondition(condition, conditional.Line, conditional.Column, "conditional expression condition");
    }

    private IrExpression RequireBooleanCondition(IrExpression expression, int line, int column, string context)
    {
        ValidateBooleanContext(expression, line, column, context);
        if (expression is IrTruthinessExpression or IrLiteralExpression { Value: bool }) return expression;
        return new IrTruthinessExpression(expression, IrTypeRef.Primitive("bool"));
    }

    private void ValidateBooleanContext(IrExpression expression, int line, int column, string context)
    {
        if (TryInferStaticType(expression, out var type) &&
            type.Kind == IrTypeKind.Primitive &&
            string.Equals(type.Name, "bool", StringComparison.Ordinal))
            return;

        AddDiagnostic(
            BooleanContextCode,
            $"{context} requires a bool expression; use '?expression' to test truthiness.",
            line,
            column);
    }

    private IrExpression LowerExpression(ExpressionNode node)
    {
        return node switch
        {
            LiteralExpressionNode literal => new IrLiteralExpression(literal.Value),
            IdentifierExpressionNode identifier => LowerIdentifier(identifier),
            ThisExpressionNode => new IrIdentifierExpression("this"),
            ParenthesizedExpressionNode parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionNode unary when unary.Operator == "?" => LowerTruthiness(unary),
            UnaryExpressionNode unary => LowerUnary(unary),
            BinaryExpressionNode binary => LowerBinary(binary),
            ConditionalExpressionNode conditional => new IrConditionalExpression(
                LowerConditionalCondition(conditional),
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
        if (TryGetCalleePath(member, out var path))
        {
            if (_externalSymbols.TryGetValue(path, out var external))
                return new IrIdentifierExpression(external);
            if (TryLowerEnumPath(path, out var enumExpression))
                return enumExpression;
            if (TryResolveEnumOwner(path, out var enumOwner))
            {
                AddDiagnostic(UnknownMemberCode, $"Enum '{enumOwner.Name}' has no value or field referenced by '{path}'.", member.Line, member.Column);
                return new IrLiteralExpression(null);
            }
            if (IsModuleQualified(path))
            {
                AddDiagnostic("SUSHI1043", $"Module member '{path}' is not exported.", member.Line, member.Column);
                return new IrLiteralExpression(null);
            }
        }

        if (TryResolveExpressionObjectType(member.Object, out var objectType))
        {
            if (_enums.TryGetValue(objectType, out var enumType))
            {
                var storageName = member.MemberName switch
                {
                    "name" => NativeObjectMetadata.EnumName,
                    "ordinal" => NativeObjectMetadata.EnumOrdinal,
                    "value" => NativeObjectMetadata.EnumValue,
                    _ => member.MemberName
                };
                if (!IsEnumField(enumType, storageName))
                    AddDiagnostic(UnknownMemberCode, $"Enum '{enumType.Name}' has no field '{member.MemberName}'.", member.Line, member.Column);
                return new IrMemberAccessExpression(
                    LowerExpression(member.Object),
                    storageName,
                    GetEnumFieldType(enumType, storageName, member.Line, member.Column));
            }

            if (_classes.TryGetValue(objectType, out var classType))
            {
                var field = classType.Fields.FirstOrDefault(field => field.Name == member.MemberName);
                if (field == null)
                    AddDiagnostic(UnknownMemberCode, $"Class '{classType.Name}' has no field '{member.MemberName}'.", member.Line, member.Column);
                else
                    return new IrMemberAccessExpression(
                        LowerExpression(member.Object),
                        member.MemberName,
                        LowerDeclaredType(field.Type, field.Line, field.Column, $"field '{field.Name}'"));
            }
        }
        var target = LowerExpression(member.Object);
        if (TryInferStaticType(target, out var targetType) && targetType.Kind == IrTypeKind.Structural)
        {
            var field = targetType.StructuralFields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field != null)
                return new IrMemberAccessExpression(target, member.MemberName, field.Type);
        }
        return new IrMemberAccessExpression(target, member.MemberName);
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
                if (TryGetCalleePath(member, out var exportedPath) &&
                    (_externalSymbols.ContainsKey(exportedPath) || IsModuleQualified(exportedPath)))
                {
                    AddDiagnostic(ReadOnlyExportCode, $"Imported module member '{exportedPath}' is read-only.", node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
                if (TryGetCalleePath(member, out var enumPath) && TryResolveEnumPath(enumPath, out _, out _, out _))
                {
                    AddDiagnostic(ImmutableValueCode, "Enum values are immutable.", node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
                if (TryResolveExpressionObjectType(member.Object, out var assignedType))
                {
                    if (_enums.ContainsKey(assignedType) && !_allowEnumMutation)
                    {
                        AddDiagnostic(ImmutableValueCode, "Enum values are immutable.", node.Line, node.Column);
                        return new IrLiteralExpression(null);
                    }
                    if (_classes.TryGetValue(assignedType, out var assignedClass) &&
                        assignedClass.Fields.All(field => field.Name != member.MemberName))
                    {
                        AddDiagnostic(UnknownMemberCode, $"Class '{assignedClass.Name}' has no field '{member.MemberName}'.", node.Line, node.Column);
                        return new IrLiteralExpression(null);
                    }
                }
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

            var assignmentValue = LowerExpression(node.Right);
            if (node.Operator == "=")
            {
                ValidateVariableAssignment(identifier.Name, assignmentValue, node.Line, node.Column);
                if (!_knownObjectTypes.ContainsKey(identifier.Name))
                    TrackVariableObjectType(identifier.Name, null, node.Right);
            }

            return new IrAssignmentExpression(
                new IrIdentifierExpression(identifier.Name),
                node.Operator,
                assignmentValue);
        }

        if (node.Operator is "==" or "===" or "!=" or "!==" &&
            TryResolveExpressionObjectType(node.Left, out var leftObjectType) &&
            TryResolveExpressionObjectType(node.Right, out var rightObjectType) &&
            (_enums.ContainsKey(leftObjectType) || _enums.ContainsKey(rightObjectType)))
        {
            if (leftObjectType != rightObjectType)
                return new IrLiteralExpression(node.Operator is "!=" or "!==");
            var equality = new IrBinaryExpression(
                new IrMemberAccessExpression(LowerExpression(node.Left), NativeObjectMetadata.EnumOrdinal),
                node.Operator is "!=" or "!==" ? "!=" : "==",
                new IrMemberAccessExpression(LowerExpression(node.Right), NativeObjectMetadata.EnumOrdinal));
            return equality;
        }

        var left = LowerExpression(node.Left);
        var right = LowerExpression(node.Right);
        if (node.Operator is "&&" or "||")
        {
            ValidateBooleanContext(left, node.Left.Line, node.Left.Column, $"left operand of '{node.Operator}'");
            ValidateBooleanContext(right, node.Right.Line, node.Right.Column, $"right operand of '{node.Operator}'");
        }
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
            if (calleePath.StartsWith("std.", StringComparison.Ordinal) &&
                _standardImportedPaths.Count > 0 &&
                !_standardImportedPaths.Contains(calleePath) &&
                !_standardImportedPaths.Any(path => calleePath.StartsWith(path + ".", StringComparison.Ordinal)))
            {
                AddDiagnostic("SUSHI1057", $"Standard-library API '{calleePath}' must be explicitly imported with a 'use' declaration.", node.Line, node.Column);
            }
            calleePath = ResolveStandardImport(calleePath);
            if (node.Callee is IdentifierExpressionNode &&
                loweredArguments.Count == 1 &&
                TryResolveExpressionObjectType(node.Arguments[0].Value, out var adaptedType))
            {
                var adapterName = AdapterName(adaptedType, calleePath);
                if (_functionSignatures.ContainsKey(adapterName))
                {
                    return new IrAdapterCallExpression(adaptedType, calleePath, adapterName, loweredArguments[0].Value);
                }

                if (IsTypeName(calleePath))
                {
                    AddDiagnostic(MissingAdapterCode, $"Type '{DisplayTypeName(adaptedType)}' does not define a '{calleePath}' adapter.", node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
            }

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
                    binding.OrderedArguments,
                    signature.ReturnType);
            }

            if (_externalSymbols.TryGetValue(calleePath, out var externalCallee))
            {
                if (_functionSignatures.TryGetValue(externalCallee, out var externalSignature))
                {
                    var binding = FunctionCallBinder.Bind(
                        calleePath,
                        externalSignature.Parameters,
                        loweredArguments,
                        _sourcePath,
                        node.Line,
                        node.Column);
                    _diagnostics.AddRange(binding.Diagnostics);
                    if (!binding.Success) return new IrLiteralExpression(null);
                    ValidateCallTypes(calleePath, externalSignature.Parameters, binding.OrderedArguments);
                    return new IrCallExpression(externalCallee, binding.OrderedArguments);
                }
                if (loweredArguments.Any(argument => argument.Name != null))
                {
                    AddDiagnostic(UnresolvedNamedCallCode, $"'{calleePath}' is not a callable exported function.", node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
                return new IrCallExpression(externalCallee, loweredArguments);
            }

            if (IsModuleQualified(calleePath) &&
                !(node.Callee is MemberAccessExpressionNode qualifiedMethod &&
                  TryResolveExpressionObjectType(qualifiedMethod.Object, out _)))
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
            if (TryResolveExpressionObjectType(memberCallee.Object, out var objectType))
            {
                var typeDeclarationMethods = _classes.TryGetValue(objectType, out var classDeclaration)
                    ? classDeclaration.Methods
                    : _enums.TryGetValue(objectType, out var enumDeclaration)
                        ? enumDeclaration.Methods
                        : new List<FunctionDeclarationNode>();
                var method = typeDeclarationMethods.FirstOrDefault(candidate => candidate.Name == memberCallee.MemberName);
                if (method == null)
                {
                    AddDiagnostic(UnknownMemberCode, $"Type '{DisplayTypeName(objectType)}' has no method '{memberCallee.MemberName}'.", node.Line, node.Column);
                    return new IrLiteralExpression(null);
                }
                var methodName = $"{NativeObjectMetadata.MethodPrefix}{objectType}_{memberCallee.MemberName}";
                var arguments = new List<IrCallArgument>
                {
                    new(null, LowerExpression(memberCallee.Object), memberCallee.Line, memberCallee.Column)
                };
                arguments.AddRange(loweredArguments);
                if (_functionSignatures.TryGetValue(methodName, out var methodSignature))
                {
                    var binding = FunctionCallBinder.Bind(methodName, methodSignature.Parameters, arguments, _sourcePath, node.Line, node.Column);
                    _diagnostics.AddRange(binding.Diagnostics);
                    if (!binding.Success) return new IrLiteralExpression(null);
                    ValidateCallTypes(methodName, methodSignature.Parameters, binding.OrderedArguments);
                    return new IrResolvedMethodCallExpression(
                        objectType,
                        memberCallee.MemberName,
                        methodName,
                        binding.OrderedArguments[0].Value,
                        binding.OrderedArguments.Skip(1));
                }
                return new IrResolvedMethodCallExpression(
                    objectType,
                    memberCallee.MemberName,
                    methodName,
                    arguments[0].Value,
                    loweredArguments);
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
                    binding.OrderedArguments,
                    stringSignature.ReturnType);
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
        if (!_classes.ContainsKey(resolvedTypeName))
        {
            AddDiagnostic(
                IsModuleQualified(node.TypeName) ? "SUSHI1043" : UnknownTypeCode,
                IsModuleQualified(node.TypeName)
                    ? $"Module class '{node.TypeName}' is not exported."
                    : $"Unknown class '{node.TypeName}'.",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }
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
            return new IrConstructionExpression(resolvedTypeName, constructorName, binding.OrderedArguments);
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

        return new IrConstructionExpression(resolvedTypeName, constructorName, loweredArguments);
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
                TrackFunctionReturnType(ResolveTopLevel(function.Name), function.ReturnType);
            }
            else if (declaration is ExportDeclarationNode { Declaration: FunctionDeclarationNode exportedFunction })
            {
                _functionSignatures[ResolveTopLevel(exportedFunction.Name)] = BuildFunctionSignature(exportedFunction);
                TrackFunctionReturnType(ResolveTopLevel(exportedFunction.Name), exportedFunction.ReturnType);
            }
        }

        foreach (var external in _externalFunctions)
        {
            _functionSignatures[external.Key] = BuildFunctionSignature(external.Value);
            TrackFunctionReturnType(external.Key, external.Value.ReturnType);
        }

        foreach (var classEntry in _classes)
        {
            var classDeclaration = classEntry.Value;
            var resolvedClassName = classEntry.Key == classDeclaration.Name
                ? ResolveTopLevel(classDeclaration.Name)
                : classEntry.Key;
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
                var methodName = $"{NativeObjectMetadata.MethodPrefix}{resolvedClassName}_{method.Name}";
                var methodParameters = new List<IrFunctionParameter>
                {
                    new("this", false, null, IrTypeRef.Primitive("object"))
                };
                methodParameters.AddRange(BuildParameterList(method.Parameters, methodName));
                _functionSignatures[methodName] = new IrFunctionSignature(
                    LowerDeclaredType(method.ReturnType, method.Line, method.Column, $"return type for method '{method.Name}'"),
                    methodParameters);
                TrackFunctionReturnType(methodName, method.ReturnType);
            }
            foreach (var adapter in classDeclaration.TypeAdapters)
            {
                var adapterName = AdapterName(resolvedClassName, adapter.TargetType);
                _functionSignatures[adapterName] = new IrFunctionSignature(
                    LowerDeclaredType(adapter.TargetType, adapter.Line, adapter.Column, $"adapter on '{classDeclaration.Name}'"),
                    new List<IrFunctionParameter> { new("this", false, null, IrTypeRef.Primitive("object")) });
            }
        }

        foreach (var enumEntry in _enums)
        {
            var enumDeclaration = enumEntry.Value;
            var resolvedEnumName = enumEntry.Key == enumDeclaration.Name
                ? ResolveTopLevel(enumDeclaration.Name)
                : enumEntry.Key;
            foreach (var method in enumDeclaration.Methods)
            {
                var methodName = $"{NativeObjectMetadata.MethodPrefix}{resolvedEnumName}_{method.Name}";
                var methodParameters = new List<IrFunctionParameter>
                {
                    new("this", false, null, IrTypeRef.Primitive("object"))
                };
                methodParameters.AddRange(BuildParameterList(method.Parameters, methodName));
                _functionSignatures[methodName] = new IrFunctionSignature(
                    LowerDeclaredType(method.ReturnType, method.Line, method.Column, $"return type for method '{method.Name}'"),
                    methodParameters);
                TrackFunctionReturnType(methodName, method.ReturnType);
            }
            foreach (var adapter in enumDeclaration.TypeAdapters)
            {
                var adapterName = AdapterName(resolvedEnumName, adapter.TargetType);
                _functionSignatures[adapterName] = new IrFunctionSignature(
                    LowerDeclaredType(adapter.TargetType, adapter.Line, adapter.Column, $"adapter on '{enumDeclaration.Name}'"),
                    new List<IrFunctionParameter> { new("this", false, null, IrTypeRef.Primitive("object")) });
            }
        }
    }

    /// <summary>
    /// Resolves omitted return annotations before lowering bodies.  This deliberately
    /// operates on named declarations only: lambdas remain dynamically typed until a
    /// future function-type feature gives them a source-level annotation site.
    /// </summary>
    private void InferNamedFunctionReturnTypes(ProgramNode program)
    {
        var callables = CollectNamedCallables(program);
        if (callables.Count == 0) return;

        // Calls may refer to functions declared later (and recursive functions), so
        // repeatedly refine signatures until no new concrete return type is learned.
        for (var pass = 0; pass < callables.Count + 2; pass++)
        {
            var changed = false;
            foreach (var callable in callables.Where(candidate => candidate.Node.ReturnType == null))
            {
                var inference = InferReturnType(callable);
                if (inference.Conflict || inference.Type.IsAnyOrUnknown ||
                    !_functionSignatures.TryGetValue(callable.EmittedName, out var existing) ||
                    SameType(existing.ReturnType, inference.Type))
                {
                    continue;
                }

                _functionSignatures[callable.EmittedName] = new IrFunctionSignature(inference.Type, existing.Parameters);
                if (inference.Type.Kind == IrTypeKind.Primitive &&
                    TryResolveDeclaredObjectType(inference.Type.Name, out var objectType))
                    _functionObjectReturnTypes[callable.EmittedName] = objectType;
                changed = true;
            }
            if (!changed) break;
        }

        foreach (var callable in callables)
        {
            if (callable.Node.ReturnType != null)
            {
                ValidateExplicitReturnShape(callable);
                continue;
            }

            var inference = InferReturnType(callable);
            if (inference.Conflict)
            {
                AddDiagnostic(
                    InferredTypeConflictCode,
                    $"Cannot infer one return type for function '{callable.DisplayName}'; return values have incompatible types. Add an explicit 'any' return type to allow mixed values.",
                    callable.Node.Line,
                    callable.Node.Column);
            }
        }
    }

    private List<NamedCallable> CollectNamedCallables(ProgramNode program)
    {
        var result = new List<NamedCallable>();
        foreach (var raw in program.Declarations)
        {
            var declaration = raw is ExportDeclarationNode export ? export.Declaration : raw;
            switch (declaration)
            {
                case FunctionDeclarationNode function:
                    result.Add(new NamedCallable(ResolveTopLevel(function.Name), function, function.Name));
                    break;
                case ClassDeclarationNode @class:
                {
                    var typeName = ResolveTopLevel(@class.Name);
                    result.AddRange(@class.Methods.Select(method => new NamedCallable(
                        $"{NativeObjectMetadata.MethodPrefix}{typeName}_{method.Name}", method, $"{@class.Name}.{method.Name}")));
                    break;
                }
                case EnumDeclarationNode @enum:
                {
                    var typeName = ResolveTopLevel(@enum.Name);
                    result.AddRange(@enum.Methods.Select(method => new NamedCallable(
                        $"{NativeObjectMetadata.MethodPrefix}{typeName}_{method.Name}", method, $"{@enum.Name}.{method.Name}")));
                    break;
                }
            }
        }
        return result;
    }

    private ReturnInference InferReturnType(NamedCallable callable)
    {
        var locals = new Dictionary<string, IrTypeRef>(StringComparer.Ordinal);
        if (_functionSignatures.TryGetValue(callable.EmittedName, out var signature))
            foreach (var parameter in signature.Parameters)
                locals[parameter.Name] = parameter.DeclaredType;

        var candidates = new List<IrTypeRef>();
        CollectReturnCandidates(callable.Node.Body, locals, candidates);
        if (!AlwaysReturns(callable.Node.Body)) candidates.Add(IrTypeRef.Void);
        return MergeReturnTypes(candidates);
    }

    private void ValidateExplicitReturnShape(NamedCallable callable)
    {
        if (!_functionSignatures.TryGetValue(callable.EmittedName, out var signature)) return;
        var isVoid = SameType(signature.ReturnType, IrTypeRef.Void);
        if (isVoid)
        {
            return;
        }

        if (signature.ReturnType.IsAnyOrUnknown) return;
        if (!AlwaysReturns(callable.Node.Body))
            AddDiagnostic(MissingReturnValueCode, $"Function '{callable.DisplayName}' can complete without returning a value of type '{DescribeType(signature.ReturnType)}'.", callable.Node.Line, callable.Node.Column);
    }

    private void CollectReturnCandidates(StatementNode statement, Dictionary<string, IrTypeRef> locals, List<IrTypeRef> candidates)
    {
        switch (statement)
        {
            case ReturnStatementNode returned:
                candidates.Add(returned.Expression == null ? IrTypeRef.Void : InferAstExpressionType(returned.Expression, locals));
                return;
            case BlockStatementNode block:
                foreach (var item in block.Statements)
                {
                    if (item is VariableDeclarationStatementNode variable && variable.Initializer != null)
                        locals[variable.Name] = InferAstExpressionType(variable.Initializer, locals);
                    CollectReturnCandidates(item, locals, candidates);
                }
                return;
            case IfStatementNode conditional:
                CollectReturnCandidates(conditional.ThenBranch, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates);
                if (conditional.ElseBranch != null)
                    CollectReturnCandidates(conditional.ElseBranch, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates);
                return;
            case WhileStatementNode loop: CollectReturnCandidates(loop.Body, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates); return;
            case ForStatementNode loop: CollectReturnCandidates(loop.Body, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates); return;
            case DoWhileStatementNode loop: CollectReturnCandidates(loop.Body, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates); return;
            case ForRangeStatementNode loop: CollectReturnCandidates(loop.Body, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates); return;
            case ForEachStatementNode loop: CollectReturnCandidates(loop.Body, new Dictionary<string, IrTypeRef>(locals, StringComparer.Ordinal), candidates); return;
        }
    }

    private IrTypeRef InferAstExpressionType(ExpressionNode expression, IReadOnlyDictionary<string, IrTypeRef> locals)
    {
        switch (expression)
        {
            case LiteralExpressionNode literal: return InferLiteralType(literal.Value);
            case InterpolatedStringExpressionNode: return IrTypeRef.Primitive("string");
            case ArrayLiteralExpressionNode: return IrTypeRef.Primitive("array");
            case ObjectLiteralExpressionNode: return IrTypeRef.Primitive("object");
            case ParenthesizedExpressionNode parenthesized: return InferAstExpressionType(parenthesized.Expression, locals);
            case IdentifierExpressionNode identifier when locals.TryGetValue(identifier.Name, out var type): return type;
            case ThisExpressionNode: return IrTypeRef.Primitive("object");
            case NewExpressionNode constructed:
                return TryResolveDeclaredObjectType(constructed.TypeName, out var objectType) ? IrTypeRef.Primitive(objectType) : IrTypeRef.Unknown;
            case UnaryExpressionNode { Operator: "!" }: return IrTypeRef.Primitive("bool");
            case ConditionalExpressionNode conditional:
                return MergeReturnTypes(new[] { InferAstExpressionType(conditional.TrueExpression, locals), InferAstExpressionType(conditional.FalseExpression, locals) }).Type;
            case BinaryExpressionNode binary when binary.Operator is "==" or "===" or "!=" or "!==" or "<" or ">" or "<=" or ">=" or "&&" or "||":
                return IrTypeRef.Primitive("bool");
            case BinaryExpressionNode binary when binary.Operator is "=" or "+=" or "-=" or "*=" or "/=":
                return InferAstExpressionType(binary.Right, locals);
            case BinaryExpressionNode binary:
                return InferBinaryType(binary, locals);
            case CallExpressionNode call when TryGetCalleePath(call.Callee, out var callee):
                if (_intrinsicRegistry.TryResolve(callee, out var intrinsic)) return intrinsic.ReturnType;
                return _functionSignatures.TryGetValue(ResolveCallable(callee), out var callable) ? callable.ReturnType : IrTypeRef.Unknown;
            default:
                return IrTypeRef.Unknown;
        }
    }

    private IrTypeRef InferBinaryType(BinaryExpressionNode binary, IReadOnlyDictionary<string, IrTypeRef> locals)
    {
        var left = InferAstExpressionType(binary.Left, locals);
        var right = InferAstExpressionType(binary.Right, locals);
        if (binary.Operator == "+" && (SameType(left, IrTypeRef.Primitive("string")) || SameType(right, IrTypeRef.Primitive("string"))))
            return IrTypeRef.Primitive("string");
        return MergeReturnTypes(new[] { left, right }).Type;
    }

    private static ReturnInference MergeReturnTypes(IEnumerable<IrTypeRef> candidates)
    {
        IrTypeRef? result = null;
        foreach (var candidate in candidates)
        {
            if (candidate.IsAnyOrUnknown) continue;
            if (result == null) { result = candidate; continue; }
            if (SameType(result, candidate)) continue;
            if (SameType(result, IrTypeRef.Primitive("int")) && SameType(candidate, IrTypeRef.Primitive("float"))) { result = IrTypeRef.Primitive("float"); continue; }
            if (SameType(result, IrTypeRef.Primitive("float")) && SameType(candidate, IrTypeRef.Primitive("int"))) continue;
            return new ReturnInference(IrTypeRef.Unknown, true);
        }
        return new ReturnInference(result ?? IrTypeRef.Unknown, false);
    }

    private static bool AlwaysReturns(StatementNode statement) => statement switch
    {
        ReturnStatementNode => true,
        BlockStatementNode block when block.Statements.Count > 0 => AlwaysReturns(block.Statements[^1]),
        IfStatementNode conditional when conditional.ElseBranch != null => AlwaysReturns(conditional.ThenBranch) && AlwaysReturns(conditional.ElseBranch),
        _ => false
    };

    private static bool SameType(IrTypeRef left, IrTypeRef right) =>
        left.Kind == right.Kind && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private void CollectTypes(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            var effectiveDeclaration = declaration is ExportDeclarationNode export ? export.Declaration : declaration;
            switch (effectiveDeclaration)
            {
                case ClassDeclarationNode classDeclaration:
                    _classes[ResolveTopLevel(classDeclaration.Name)] = classDeclaration;
                    break;
                case EnumDeclarationNode enumDeclaration:
                    _enums[ResolveTopLevel(enumDeclaration.Name)] = enumDeclaration;
                    break;
            }
        }

        foreach (var external in _externalClasses) _classes[external.Key] = external.Value;
        foreach (var external in _externalEnums) _enums[external.Key] = external.Value;
    }

    private void ValidateTypeDeclarations(ProgramNode program)
    {
        foreach (var raw in program.Declarations)
        {
            var declaration = raw is ExportDeclarationNode export ? export.Declaration : raw;
            if (declaration is ClassDeclarationNode @class)
            {
                ReportDuplicateNames(@class.Fields.Select(field => (field.Name, field.Line, field.Column)), "field", @class.Name);
                ReportDuplicateNames(@class.Methods.Select(method => (method.Name, method.Line, method.Column)), "method", @class.Name);
                ReportDuplicateNames(@class.TypeAdapters.Select(adapter => (adapter.TargetType, adapter.Line, adapter.Column)), "adapter", @class.Name);
            }
            else if (declaration is EnumDeclarationNode @enum)
            {
                ReportDuplicateNames(@enum.Values.Select(value => (value.Name, value.Line, value.Column)), "value", @enum.Name);
                ReportDuplicateNames(@enum.Methods.Select(method => (method.Name, method.Line, method.Column)), "method", @enum.Name);
                ReportDuplicateNames(@enum.TypeAdapters.Select(adapter => (adapter.TargetType, adapter.Line, adapter.Column)), "adapter", @enum.Name);
            }
        }
    }

    private void ReportDuplicateNames(IEnumerable<(string Name, int Line, int Column)> names, string kind, string typeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in names)
            if (!seen.Add(item.Name))
                AddDiagnostic(UnknownMemberCode, $"Type '{typeName}' declares duplicate {kind} '{item.Name}'.", item.Line, item.Column);
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

    private static string AdapterName(string resolvedType, string targetType) =>
        $"__sushi_adapter_{resolvedType}_{targetType.ToLowerInvariant()}";

    private static bool IsTypeName(string name) => name.ToLowerInvariant() is
        "string" or "str" or "int" or "integer" or "float" or "double" or "decimal" or
        "number" or "bool" or "boolean" or "array" or "list" or "object" or "map";

    private static string DisplayTypeName(string resolvedType)
    {
        var separator = resolvedType.LastIndexOf('_');
        return separator >= 0 ? resolvedType[(separator + 1)..] : resolvedType;
    }

    private void RestoreKnownObjectTypes(Dictionary<string, string> snapshot)
    {
        _knownObjectTypes.Clear();
        foreach (var item in snapshot) _knownObjectTypes[item.Key] = item.Value;
    }

    private void RestoreKnownVariableTypes(Dictionary<string, IrTypeRef> snapshot)
    {
        _knownVariableTypes.Clear();
        foreach (var item in snapshot) _knownVariableTypes[item.Key] = item.Value;
    }

    private void DeclareVariableType(string name, string? declaredType, IrExpression? initializer, int line, int column)
    {
        var declared = LowerDeclaredType(declaredType, 1, 1, $"variable '{name}'");
        if (declaredType != null)
        {
            _knownVariableTypes[name] = declared;
            if (initializer != null && !declared.IsAnyOrUnknown)
            {
                ValidateExpressionAgainstType(
                    initializer,
                    declared,
                    line,
                    column,
                    $"initializer for variable '{name}'",
                    InferredTypeConflictCode);
            }
            return;
        }

        if (initializer != null && TryInferStaticType(initializer, out var inferred))
        {
            _knownVariableTypes[name] = inferred;
        }
        else
        {
            _knownVariableTypes.Remove(name);
        }
    }

    private void ValidateVariableAssignment(string name, IrExpression value, int line, int column)
    {
        if (!_knownVariableTypes.TryGetValue(name, out var expectedType))
        {
            if (TryInferStaticType(value, out var inferredType))
                _knownVariableTypes[name] = inferredType;
            return;
        }

        ValidateExpressionAgainstType(
            value,
            expectedType,
            line,
            column,
            $"assignment to variable '{name}'",
            InferredTypeConflictCode);
    }

    private void TrackVariableObjectType(string name, string? declaredType, ExpressionNode? initializer)
    {
        if (TryResolveDeclaredObjectType(declaredType, out var declaredObjectType))
        {
            _knownObjectTypes[name] = declaredObjectType;
            return;
        }
        if (initializer != null && TryResolveExpressionObjectType(initializer, out var inferredType))
            _knownObjectTypes[name] = inferredType;
        else
            _knownObjectTypes.Remove(name);
    }

    private bool TryResolveExpressionObjectType(ExpressionNode expression, out string resolvedType)
    {
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                return TryResolveExpressionObjectType(parenthesized.Expression, out resolvedType);
            case NewExpressionNode constructed:
                resolvedType = ResolveCallable(constructed.TypeName);
                return _classes.ContainsKey(resolvedType);
            case IdentifierExpressionNode identifier when _knownObjectTypes.TryGetValue(identifier.Name, out var known):
                resolvedType = known;
                return true;
            case ThisExpressionNode when _knownObjectTypes.TryGetValue("this", out var thisType):
                resolvedType = thisType;
                return true;
            case CallExpressionNode { Callee: MemberAccessExpressionNode memberCall }
                when TryResolveExpressionObjectType(memberCall.Object, out var receiverType):
            {
                var method = _classes.TryGetValue(receiverType, out var receiverClass)
                    ? receiverClass.Methods.FirstOrDefault(candidate => candidate.Name == memberCall.MemberName)
                    : _enums.TryGetValue(receiverType, out var receiverEnum)
                        ? receiverEnum.Methods.FirstOrDefault(candidate => candidate.Name == memberCall.MemberName)
                        : null;
                if (method != null && TryResolveDeclaredObjectType(method.ReturnType, out resolvedType)) return true;
                break;
            }
            case CallExpressionNode call when TryGetCalleePath(call.Callee, out var callee):
                if (_functionObjectReturnTypes.TryGetValue(ResolveCallable(callee), out var returnType))
                {
                    resolvedType = returnType;
                    return true;
                }
                break;
            case MemberAccessExpressionNode member when TryGetCalleePath(member, out var path):
                return TryResolveEnumPath(path, out resolvedType, out _, out _);
            case ConditionalExpressionNode conditional
                when TryResolveExpressionObjectType(conditional.TrueExpression, out var trueType) &&
                     TryResolveExpressionObjectType(conditional.FalseExpression, out var falseType) &&
                     trueType == falseType:
                resolvedType = trueType;
                return true;
            default:
                resolvedType = "";
                return false;
        }
        resolvedType = "";
        return false;
    }

    private bool TryResolveEnumPath(string path, out string enumType, out string valueName, out IReadOnlyList<string> remaining)
    {
        var parts = path.Split('.');
        for (var ownerLength = Math.Min(2, parts.Length - 1); ownerLength >= 1; ownerLength--)
        {
            var owner = string.Join('.', parts.Take(ownerLength));
            var resolvedOwner = ResolveCallable(owner);
            if (!_enums.TryGetValue(resolvedOwner, out var declaration)) continue;
            var candidateValue = parts[ownerLength];
            if (declaration.Values.All(value => value.Name != candidateValue)) continue;
            enumType = resolvedOwner;
            valueName = candidateValue;
            remaining = parts.Skip(ownerLength + 1).ToArray();
            return true;
        }
        enumType = "";
        valueName = "";
        remaining = Array.Empty<string>();
        return false;
    }

    private bool TryResolveEnumOwner(string path, out EnumDeclarationNode declaration)
    {
        var parts = path.Split('.');
        for (var length = Math.Min(2, parts.Length); length >= 1; length--)
        {
            var owner = ResolveCallable(string.Join('.', parts.Take(length)));
            if (_enums.TryGetValue(owner, out declaration!)) return true;
        }
        declaration = null!;
        return false;
    }

    private bool TryLowerEnumPath(string path, out IrExpression expression)
    {
        if (!TryResolveEnumPath(path, out var type, out var value, out var remaining))
        {
            expression = null!;
            return false;
        }
        expression = new IrIdentifierExpression($"{type}_{value}");
        foreach (var member in remaining)
        {
            var storageName = member switch
            {
                "name" => NativeObjectMetadata.EnumName,
                "ordinal" => NativeObjectMetadata.EnumOrdinal,
                "value" => NativeObjectMetadata.EnumValue,
                _ => member
            };
            expression = new IrMemberAccessExpression(
                expression,
                storageName,
                GetEnumFieldType(_enums[type], storageName, 1, 1));
        }
        return true;
    }

    private static bool IsEnumField(EnumDeclarationNode declaration, string field) =>
        field is NativeObjectMetadata.EnumName or NativeObjectMetadata.EnumOrdinal or NativeObjectMetadata.EnumValue or NativeObjectMetadata.Type ||
        declaration.RecordParameters?.Any(parameter => parameter.Name == field) == true ||
        declaration.Values.Any(value => value.Properties?.ContainsKey(field) == true) ||
        declaration.ExplicitConstructor != null;

    private IrTypeRef GetEnumFieldType(EnumDeclarationNode declaration, string field, int line, int column)
    {
        if (field == NativeObjectMetadata.EnumName) return IrTypeRef.Primitive("string");
        if (field == NativeObjectMetadata.EnumOrdinal) return IrTypeRef.Primitive("int");
        if (field == NativeObjectMetadata.EnumValue)
        {
            var values = declaration.Values.Where(value => value.DirectValue != null).Select(value => LowerExpression(value.DirectValue!)).ToList();
            if (values.Count > 0 && values.All(value => TryInferStaticType(value, out var type) && type.Name == "int"))
                return IrTypeRef.Primitive("int");
            return IrTypeRef.Any;
        }
        var parameter = declaration.RecordParameters?.FirstOrDefault(candidate => candidate.Name == field);
        if (parameter != null)
            return LowerDeclaredType(parameter.Type, parameter.Line, parameter.Column, $"enum field '{field}'");
        var inlineValues = declaration.Values
            .Where(value => value.Properties?.ContainsKey(field) == true)
            .Select(value => LowerExpression(value.Properties![field]))
            .ToList();
        if (inlineValues.Count > 0 &&
            inlineValues.All(value => TryInferStaticType(value, out _)) &&
            TryInferStaticType(inlineValues[0], out var inlineType) &&
            inlineValues.All(value => TryInferStaticType(value, out var valueType) && IsTypeAssignable(inlineType, valueType)))
            return inlineType;
        if (declaration.ExplicitConstructor != null &&
            TryGetConstructorFieldType(declaration.ExplicitConstructor, field, out var constructorType))
            return constructorType;
        return IrTypeRef.Any;
    }

    private bool TryGetConstructorFieldType(ConstructorDeclarationNode constructor, string field, out IrTypeRef type)
    {
        foreach (var statement in constructor.Body.Statements)
        {
            if (statement is not ExpressionStatementNode
                {
                    Expression: BinaryExpressionNode
                    {
                        Operator: "=",
                        Left: MemberAccessExpressionNode { Object: ThisExpressionNode } member,
                        Right: var value
                    }
                } || member.MemberName != field)
                continue;
            if (value is IdentifierExpressionNode identifier)
            {
                var parameter = constructor.Parameters.FirstOrDefault(candidate => candidate.Name == identifier.Name);
                if (parameter != null)
                {
                    type = LowerDeclaredType(parameter.Type, parameter.Line, parameter.Column, $"enum field '{field}'");
                    return true;
                }
            }
            var lowered = LowerExpression(value);
            if (TryInferStaticType(lowered, out type)) return true;
        }
        type = IrTypeRef.Any;
        return false;
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
        // Within a class method/constructor, fields are implicitly addressed
        // through the current receiver.  Keep `this.field` available, but let
        // `field` remain idiomatic source syntax.
        if (_knownObjectTypes.TryGetValue("this", out var currentType) &&
            _classes.TryGetValue(currentType, out var currentClass))
        {
            var field = currentClass.Fields.FirstOrDefault(candidate => candidate.Name == identifier.Name);
            if (field != null && !_definedVariables.Contains(identifier.Name))
            {
                return new IrMemberAccessExpression(
                    new IrIdentifierExpression("this"),
                    field.Name,
                    LowerDeclaredType(field.Type, field.Line, field.Column, $"field '{field.Name}'"));
            }
        }

        ValidateIdentifier(identifier);
        if (_enums.ContainsKey(ResolveCallable(identifier.Name)))
        {
            AddDiagnostic(UnknownMemberCode, $"Enum type '{identifier.Name}' is not a value; select one of its declared values.", identifier.Line, identifier.Column);
            return new IrLiteralExpression(null);
        }
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
        var nativeMethods = new List<IrClassMethod>();
        var nativeAdapters = new List<IrClassMethod>();
        var legacyFunctionNames = new List<string>();

        foreach (var method in node.Methods)
        {
            var methodName = $"{NativeObjectMetadata.MethodPrefix}{resolvedTypeName}_{method.Name}";
            var parameters = new List<IrFunctionParameter>
            {
                new("this", false, null, IrTypeRef.Primitive("object"))
            };
            parameters.AddRange(BuildParameterList(method.Parameters, methodName));
            var previousVariables = _definedVariables;
            var previousObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
            var previousVariableTypes = new Dictionary<string, IrTypeRef>(_knownVariableTypes, StringComparer.Ordinal);
            var previousFunctionName = _currentFunctionName;
            var previousReturnType = _currentFunctionReturnType;
            _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
            _knownObjectTypes["this"] = resolvedTypeName;
            foreach (var parameter in parameters)
            {
                _definedVariables.Add(parameter.Name);
                _knownVariableTypes[parameter.Name] = parameter.DeclaredType;
            }
            TrackParameterObjectTypes(method.Parameters, parameters.Skip(1).ToList());
            _currentFunctionName = methodName;
            _currentFunctionReturnType = _functionSignatures[methodName].ReturnType;
            _functionDepth++;
            var body = method.Body is StatementNode statementBody
                ? StatementToBlock(statementBody)
                : new IrBlockStatement();
            _functionDepth--;
            _definedVariables = previousVariables;
            RestoreKnownObjectTypes(previousObjectTypes);
            RestoreKnownVariableTypes(previousVariableTypes);
            _currentFunctionName = previousFunctionName;
            _currentFunctionReturnType = previousReturnType;

            var loweredMethod = new IrFunctionDeclarationStatement(
                methodName,
                parameters,
                body,
                _functionSignatures[methodName].ReturnType);
            statements.Add(loweredMethod);
            legacyFunctionNames.Add(methodName);
            nativeMethods.Add(new IrClassMethod(method.Name, parameters.Skip(1), body,
                loweredMethod.ReturnType, methodName));
        }

        foreach (var adapter in node.TypeAdapters)
        {
            var loweredAdapter = (IrFunctionDeclarationStatement)LowerAdapter(resolvedTypeName, adapter);
            statements.Add(loweredAdapter);
            legacyFunctionNames.Add(loweredAdapter.Name);
            nativeAdapters.Add(new IrClassMethod(adapter.TargetType, loweredAdapter.Parameters.Skip(1),
                loweredAdapter.Body, loweredAdapter.ReturnType, loweredAdapter.Name));
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
        var previousConstructorObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal);
        _knownObjectTypes["this"] = resolvedTypeName;
        foreach (var parameter in ctorSignature.Parameters)
        {
            _definedVariables.Add(parameter.Name);
        }
        TrackParameterObjectTypes(ctorParameters, ctorSignature.Parameters);

        var objectProperties = new List<IrObjectProperty>();
        var nativeFields = new List<IrClassField>();

        foreach (var method in node.Methods)
        {
            objectProperties.Add(new IrObjectProperty(
                $"{NativeObjectMetadata.MethodPrefix}{method.Name}",
                new IrLiteralExpression($"{NativeObjectMetadata.MethodPrefix}{resolvedTypeName}_{method.Name}")));
        }

        foreach (var field in node.Fields)
        {
            var matchingCtorParameter = ctorSignature.Parameters.FirstOrDefault(p => p.Name == field.Name);
            var fieldInitializer = field.Initializer != null ? LowerExpression(field.Initializer) : null;
            var fieldType = field.Type == null && fieldInitializer != null && TryInferStaticType(fieldInitializer, out var inferredFieldType)
                ? inferredFieldType
                : LowerDeclaredType(field.Type, field.Line, field.Column, $"field '{field.Name}'");
            if (fieldInitializer != null)
            {
                ValidateExpressionAgainstType(
                    fieldInitializer,
                    fieldType,
                    field.Line,
                    field.Column,
                    $"initializer for field '{field.Name}'",
                    FieldTypeMismatchCode);
            }
            nativeFields.Add(new IrClassField(field.Name, fieldType, fieldInitializer));
            if (matchingCtorParameter != null)
            {
                objectProperties.Add(new IrObjectProperty(field.Name, new IrIdentifierExpression(field.Name)));
            }
            else if (fieldInitializer != null)
            {
                objectProperties.Add(new IrObjectProperty(field.Name, fieldInitializer));
            }
            else
            {
                objectProperties.Add(new IrObjectProperty(field.Name, new IrLiteralExpression(null)));
            }
        }

        var nativeConstructorStatements = new List<IrStatement>();
        if (node.Constructor != null)
        {
            _definedVariables.Add("this");
            _functionDepth++;
            nativeConstructorStatements.AddRange(LowerBlock(node.Constructor.Body).Statements);
            _functionDepth--;
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
        legacyFunctionNames.Add(constructorName);
        statements.Insert(0, new IrClassDeclarationStatement(resolvedTypeName, nativeFields,
            ctorSignature.Parameters, new IrBlockStatement(nativeConstructorStatements), nativeMethods,
            nativeAdapters, legacyFunctionNames));
        _definedVariables = previousConstructorVariables;
        RestoreKnownObjectTypes(previousConstructorObjectTypes);

        return new IrBlockStatement(statements);
    }

    private IrStatement LowerEnum(EnumDeclarationNode node)
    {
        var statements = new List<IrStatement>();
        var resolvedTypeName = ResolveTopLevel(node.Name);
        var nativeEnumValues = TryGetNativeEnumValues(node);
        var richEnumValues = new List<IrRichEnumValue>();
        List<IrFunctionParameter>? richConstructorParameters = null;
        IrBlockStatement? richConstructorBody = null;
        var sharedConstructorName = $"__sushi_new_{resolvedTypeName}";
        var sharedSeedPropertyNames = new List<string>
        {
            NativeObjectMetadata.EnumName,
            NativeObjectMetadata.EnumOrdinal,
            NativeObjectMetadata.EnumValue
        };
        var assignedConstructorFields = node.ExplicitConstructor != null
            ? CollectAssignedThisMembers(node.ExplicitConstructor.Body)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var enumValue in node.Values)
        {
            if (enumValue.Properties != null)
            {
                foreach (var property in enumValue.Properties)
                    if (!sharedSeedPropertyNames.Contains(property.Key, StringComparer.Ordinal))
                        sharedSeedPropertyNames.Add(property.Key);
            }
        }
        foreach (var method in node.Methods)
        {
            var metadataName = $"{NativeObjectMetadata.MethodPrefix}{method.Name}";
            if (!sharedSeedPropertyNames.Contains(metadataName, StringComparer.Ordinal))
                sharedSeedPropertyNames.Add(metadataName);
        }
        var sharedSeedParameters = sharedSeedPropertyNames
            .Select((_, index) => new IrFunctionParameter($"__sushi_enum_field_{index}", false, null, IrTypeRef.Any))
            .ToList();
        var richMethods = new List<IrClassMethod>();
        var richAdapters = new List<IrClassMethod>();
        var richLegacyFunctions = new List<string>();
        foreach (var method in node.Methods)
        {
            var methodName = $"{NativeObjectMetadata.MethodPrefix}{resolvedTypeName}_{method.Name}";
            var parameters = new List<IrFunctionParameter>
            {
                new("this", false, null, IrTypeRef.Primitive("object"))
            };
            parameters.AddRange(BuildParameterList(method.Parameters, methodName));
            var previousVariables = _definedVariables;
            var previousObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
            var previousVariableTypes = new Dictionary<string, IrTypeRef>(_knownVariableTypes, StringComparer.Ordinal);
            var previousFunctionName = _currentFunctionName;
            var previousReturnType = _currentFunctionReturnType;
            _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
            _knownObjectTypes["this"] = resolvedTypeName;
            foreach (var parameter in parameters)
            {
                _definedVariables.Add(parameter.Name);
                _knownVariableTypes[parameter.Name] = parameter.DeclaredType;
            }
            TrackParameterObjectTypes(method.Parameters, parameters.Skip(1).ToList());
            _currentFunctionName = methodName;
            _currentFunctionReturnType = _functionSignatures[methodName].ReturnType;
            _functionDepth++;
            var body = method.Body is StatementNode statementBody
                ? StatementToBlock(statementBody)
                : new IrBlockStatement();
            _functionDepth--;
            _definedVariables = previousVariables;
            RestoreKnownObjectTypes(previousObjectTypes);
            RestoreKnownVariableTypes(previousVariableTypes);
            _currentFunctionName = previousFunctionName;
            _currentFunctionReturnType = previousReturnType;

            var loweredMethod = new IrFunctionDeclarationStatement(
                methodName,
                parameters,
                body,
                _functionSignatures[methodName].ReturnType);
            statements.Add(loweredMethod);
            richMethods.Add(new IrClassMethod(method.Name, parameters.Skip(1), body, loweredMethod.ReturnType, methodName));
            richLegacyFunctions.Add(methodName);
        }

        foreach (var adapter in node.TypeAdapters)
        {
            var loweredAdapter = (IrFunctionDeclarationStatement)LowerAdapter(resolvedTypeName, adapter);
            statements.Add(loweredAdapter);
            richAdapters.Add(new IrClassMethod(adapter.TargetType, loweredAdapter.Parameters.Skip(1), loweredAdapter.Body,
                loweredAdapter.ReturnType, loweredAdapter.Name));
            richLegacyFunctions.Add(loweredAdapter.Name);
        }

        for (var ordinal = 0; ordinal < node.Values.Count; ordinal++)
        {
            var value = node.Values[ordinal];
            var valueProperties = BuildEnumValueProperties(node, resolvedTypeName, value, ordinal);
            IrExpression initializer = new IrObjectLiteralExpression(valueProperties);

            if (node.ExplicitConstructor != null)
            {
                var userParameters = BuildParameterList(node.ExplicitConstructor.Parameters, sharedConstructorName);
                var parameters = userParameters.Concat(sharedSeedParameters).ToList();
                if (richConstructorParameters == null)
                    richConstructorParameters = userParameters;

                if (richConstructorBody == null)
                {
                    var previousVariables = _definedVariables;
                    var previousObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
                    _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
                    _knownObjectTypes["this"] = resolvedTypeName;
                    foreach (var parameter in parameters) _definedVariables.Add(parameter.Name);
                    _functionDepth++;
                    var seedProperties = sharedSeedPropertyNames.Select((propertyName, index) =>
                        new IrObjectProperty(propertyName,
                            assignedConstructorFields.Contains(propertyName)
                                ? new IrLiteralExpression(null)
                                : new IrIdentifierExpression(sharedSeedParameters[index].Name))).ToList();
                    var body = new List<IrStatement>
                    {
                        new IrVariableDeclarationStatement("this", new IrObjectLiteralExpression(seedProperties))
                    };
                    var previousAllowEnumMutation = _allowEnumMutation;
                    _allowEnumMutation = true;
                    body.AddRange(LowerBlock(node.ExplicitConstructor.Body).Statements);
                    _allowEnumMutation = previousAllowEnumMutation;
                    body.Add(new IrReturnStatement(new IrIdentifierExpression("this")));
                    richConstructorBody = new IrBlockStatement(body.Skip(1).SkipLast(1));
                    _functionDepth--;
                    _definedVariables = previousVariables;
                    RestoreKnownObjectTypes(previousObjectTypes);
                    statements.Add(new IrFunctionDeclarationStatement(
                        sharedConstructorName,
                        parameters,
                        new IrBlockStatement(body),
                        IrTypeRef.Primitive("object")));
                    richLegacyFunctions.Add(sharedConstructorName);
                }
                var arguments = (value.ConstructorArgs ?? new List<ExpressionNode>())
                    .Select(argument => new IrCallArgument(null, LowerExpression(argument), argument.Line, argument.Column))
                    .ToList();
                var propertiesByName = valueProperties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                arguments.AddRange(sharedSeedPropertyNames.Select(propertyName =>
                    new IrCallArgument(null, assignedConstructorFields.Contains(propertyName)
                        ? new IrLiteralExpression(null)
                        : propertiesByName.TryGetValue(propertyName, out var propertyValue)
                        ? propertyValue
                        : new IrLiteralExpression(null), value.Line, value.Column)));
                var binding = FunctionCallBinder.Bind(sharedConstructorName, parameters, arguments, _sourcePath, value.Line, value.Column);
                _diagnostics.AddRange(binding.Diagnostics);
                if (binding.Success)
                    ValidateCallTypes(sharedConstructorName, parameters, binding.OrderedArguments);
                initializer = new IrConstructionExpression(
                    resolvedTypeName,
                    sharedConstructorName,
                    binding.Success ? binding.OrderedArguments : arguments);
            }
            else if (node.RecordParameters != null)
            {
                var parameters = BuildParameterList(node.RecordParameters, $"{resolvedTypeName}.{value.Name}");
                var arguments = (value.ConstructorArgs ?? new List<ExpressionNode>())
                    .Select(argument => new IrCallArgument(null, LowerExpression(argument), argument.Line, argument.Column))
                    .ToList();
                var binding = FunctionCallBinder.Bind($"{resolvedTypeName}.{value.Name}", parameters, arguments, _sourcePath, value.Line, value.Column);
                _diagnostics.AddRange(binding.Diagnostics);
                if (binding.Success) ValidateCallTypes($"{resolvedTypeName}.{value.Name}", parameters, binding.OrderedArguments);
            }

            if (nativeEnumValues == null)
            {
                var constructorArguments = (value.ConstructorArgs ?? new List<ExpressionNode>()).Select(LowerExpression).ToList();
                richEnumValues.Add(new IrRichEnumValue(value.Name, valueProperties, constructorArguments));
            }
            statements.Add(new IrVariableDeclarationStatement($"{resolvedTypeName}_{value.Name}", initializer));
        }

        if (nativeEnumValues != null)
        {
            statements.Insert(0, new IrEnumDeclarationStatement(resolvedTypeName, nativeEnumValues,
                node.Values.Select(value => $"{resolvedTypeName}_{value.Name}")));
        }
        else if (richEnumValues.Count == node.Values.Count)
        {
            statements.Insert(0, new IrRichEnumDeclarationStatement(resolvedTypeName, richEnumValues,
                node.Values.Select(value => $"{resolvedTypeName}_{value.Name}"), richConstructorParameters, richConstructorBody,
                richMethods, richAdapters, richLegacyFunctions));
        }

        return new IrBlockStatement(statements);
    }

    private static List<IrEnumValue>? TryGetNativeEnumValues(EnumDeclarationNode node)
    {
        if (node.RecordParameters != null || node.ExplicitConstructor != null || node.Methods.Count > 0 || node.TypeAdapters.Count > 0)
            return null;
        var output = new List<IrEnumValue>();
        var nextValue = 0;
        foreach (var value in node.Values)
        {
            if (value.Properties != null || value.ConstructorArgs != null) return null;
            if (value.DirectValue is LiteralExpressionNode { Kind: LiteralKind.Integer, Value: not null } literal)
            {
                try { nextValue = Convert.ToInt32(literal.Value, CultureInfo.InvariantCulture); }
                catch (OverflowException) { return null; }
            }
            else if (value.DirectValue != null) return null;
            if (output.Any(existing => existing.Value == nextValue)) return null;
            output.Add(new IrEnumValue(value.Name, nextValue, output.Count));
            nextValue++;
        }
        return output;
    }

    private List<IrObjectProperty> BuildEnumValueProperties(
        EnumDeclarationNode node,
        string resolvedTypeName,
        EnumValueNode value,
        int ordinal)
    {
        var properties = new List<IrObjectProperty>
        {
            new(NativeObjectMetadata.EnumName, new IrLiteralExpression(value.Name)),
            new(NativeObjectMetadata.EnumOrdinal, new IrLiteralExpression(ordinal)),
            new(NativeObjectMetadata.EnumValue, value.DirectValue != null ? LowerExpression(value.DirectValue) : new IrLiteralExpression(ordinal))
        };
        if (value.Properties != null)
            properties.AddRange(value.Properties.Select(property => new IrObjectProperty(property.Key, LowerExpression(property.Value))));
        if (node.RecordParameters != null && value.ConstructorArgs != null)
        {
            for (var index = 0; index < Math.Min(node.RecordParameters.Count, value.ConstructorArgs.Count); index++)
                properties.Add(new IrObjectProperty(node.RecordParameters[index].Name, LowerExpression(value.ConstructorArgs[index])));
        }
        if (node.ExplicitConstructor != null)
        {
            foreach (var field in CollectAssignedThisMembers(node.ExplicitConstructor.Body))
                if (properties.All(property => property.Name != field))
                    properties.Add(new IrObjectProperty(field, new IrLiteralExpression(null)));
        }
        foreach (var method in node.Methods)
            properties.Add(new IrObjectProperty($"{NativeObjectMetadata.MethodPrefix}{method.Name}", new IrLiteralExpression($"{NativeObjectMetadata.MethodPrefix}{resolvedTypeName}_{method.Name}")));
        return properties;
    }

    private static IReadOnlySet<string> CollectAssignedThisMembers(StatementNode statement)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Visit(statement);
        return fields;

        void Visit(StatementNode current)
        {
            switch (current)
            {
                case ExpressionStatementNode
                {
                    Expression: BinaryExpressionNode
                    {
                        Operator: "=" or "+=" or "-=" or "*=" or "/=",
                        Left: MemberAccessExpressionNode { Object: ThisExpressionNode } member
                    }
                }:
                    fields.Add(member.MemberName);
                    break;
                case BlockStatementNode block:
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case IfStatementNode conditional:
                    Visit(conditional.ThenBranch);
                    if (conditional.ElseBranch != null) Visit(conditional.ElseBranch);
                    break;
                case WhileStatementNode loop:
                    Visit(loop.Body);
                    break;
                case DoWhileStatementNode loop:
                    Visit(loop.Body);
                    break;
                case ForStatementNode loop:
                    Visit(loop.Body);
                    break;
                case ForRangeStatementNode loop:
                    Visit(loop.Body);
                    break;
                case ForEachStatementNode loop:
                    Visit(loop.Body);
                    break;
                case SwitchStatementNode selection:
                    foreach (var @case in selection.Cases) Visit(@case.Body);
                    if (selection.DefaultCase != null) Visit(selection.DefaultCase);
                    break;
            }
        }
    }

    private IrFunctionDeclarationStatement LowerAdapter(string resolvedTypeName, TypeAdapterDeclarationNode adapter)
    {
        var name = AdapterName(resolvedTypeName, adapter.TargetType);
        var previousVariables = _definedVariables;
        var previousObjectTypes = new Dictionary<string, string>(_knownObjectTypes, StringComparer.Ordinal);
        var previousFunctionName = _currentFunctionName;
        var previousReturnType = _currentFunctionReturnType;
        _definedVariables = new HashSet<string>(_globalVariables, StringComparer.Ordinal) { "this" };
        _knownObjectTypes["this"] = resolvedTypeName;
        _currentFunctionName = name;
        _currentFunctionReturnType = LowerDeclaredType(adapter.TargetType, adapter.Line, adapter.Column, $"adapter on '{DisplayTypeName(resolvedTypeName)}'");
        _functionDepth++;
        var body = StatementToBlock(adapter.Body);
        _functionDepth--;
        _definedVariables = previousVariables;
        RestoreKnownObjectTypes(previousObjectTypes);
        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;
        return new IrFunctionDeclarationStatement(
            name,
            new[] { new IrFunctionParameter("this", false, null, IrTypeRef.Primitive("object")) },
            body,
            LowerDeclaredType(adapter.TargetType, adapter.Line, adapter.Column, $"adapter on '{DisplayTypeName(resolvedTypeName)}'"));
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

        // Preserve C-style array annotations in the AST while lowering them
        // to Sushi's single native array representation in the IR.
        if (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            return IrTypeRef.Primitive("array");
        }

        var primitive = normalized.ToLowerInvariant() switch
        {
            "string" or "str" or "char" => IrTypeRef.Primitive("string"),
            "int" or "integer" or "long" or "short" => IrTypeRef.Primitive("int"),
            "float" or "double" or "decimal" or "number" => IrTypeRef.Primitive("float"),
            "bool" or "boolean" => IrTypeRef.Primitive("bool"),
            "array" or "list" => IrTypeRef.Primitive("array"),
            "object" or "map" or "dictionary" or "dict" => IrTypeRef.Primitive("object"),
            "any" or "var" => IrTypeRef.Any,
            "void" => IrTypeRef.Void,
            _ => null
        };
        if (primitive != null) return primitive;
        return TryResolveDeclaredObjectType(normalized, out var resolvedObjectType)
            ? IrTypeRef.Primitive(resolvedObjectType)
            : AddUnsupportedTypeAndReturnUnknown(normalized, line, column, context);
    }

    private bool TryResolveDeclaredObjectType(string? declaredType, out string resolvedType)
    {
        resolvedType = "";
        if (string.IsNullOrWhiteSpace(declaredType)) return false;
        var candidate = ResolveCallable(declaredType.Trim().TrimEnd('?'));
        if (_classes.ContainsKey(candidate) || _enums.ContainsKey(candidate))
        {
            resolvedType = candidate;
            return true;
        }
        if (_classes.ContainsKey(declaredType) || _enums.ContainsKey(declaredType))
        {
            resolvedType = ResolveTopLevel(declaredType);
            return true;
        }
        var importedMatches = _externalClasses
            .Where(item => item.Value.Name == declaredType)
            .Select(item => item.Key)
            .Concat(_externalEnums.Where(item => item.Value.Name == declaredType).Select(item => item.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (importedMatches.Count == 1)
        {
            resolvedType = importedMatches[0];
            return true;
        }
        return false;
    }

    private void TrackFunctionReturnType(string functionName, string? declaredType)
    {
        if (TryResolveDeclaredObjectType(declaredType, out var type))
            _functionObjectReturnTypes[functionName] = type;
    }

    private void TrackParameterObjectTypes(
        IReadOnlyList<ParameterNode> sourceParameters,
        IReadOnlyList<IrFunctionParameter> loweredParameters)
    {
        var count = Math.Min(sourceParameters.Count, loweredParameters.Count);
        for (var index = 0; index < count; index++)
        {
            if (TryResolveDeclaredObjectType(sourceParameters[index].Type, out var type))
                _knownObjectTypes[loweredParameters[index].Name] = type;
        }
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

        if (SameType(_currentFunctionReturnType, IrTypeRef.Void))
        {
            if (expression != null)
            {
                AddDiagnostic(
                    VoidReturnValueCode,
                    $"Void function '{_currentFunctionName}' cannot return a value.",
                    line,
                    column);
            }
            return;
        }

        if (expression == null)
        {
            AddDiagnostic(
                MissingReturnValueCode,
                $"Function '{_currentFunctionName}' must return a value of type '{DescribeType(_currentFunctionReturnType)}'.",
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
                !IsTypeAssignable(expectedType, actualType))
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

    private bool TryInferStaticType(IrExpression expression, out IrTypeRef type)
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

            case IrConstructionExpression construction:
                type = IrTypeRef.Primitive(construction.TypeName);
                return true;

            case IrMemberAccessExpression member when !member.ValueType.IsAnyOrUnknown:
                type = member.ValueType;
                return true;

            case IrIdentifierExpression identifier when _knownObjectTypes.TryGetValue(identifier.Name, out var objectType):
                type = IrTypeRef.Primitive(objectType);
                return true;

            case IrIdentifierExpression identifier when _knownVariableTypes.TryGetValue(identifier.Name, out var variableType):
                type = variableType;
                return !type.IsAnyOrUnknown;

            case IrTruthinessExpression:
                type = IrTypeRef.Primitive("bool");
                return true;

            case IrUnaryExpression { Operator: "!" }:
                type = IrTypeRef.Primitive("bool");
                return true;

            case IrBinaryExpression { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" }:
                type = IrTypeRef.Primitive("bool");
                return true;

            case IrCallExpression call when _functionSignatures.TryGetValue(call.Callee, out var signature) &&
                                                !signature.ReturnType.IsAnyOrUnknown:
                type = signature.ReturnType;
                return true;

            case IrResolvedMethodCallExpression methodCall
                when _functionSignatures.TryGetValue(methodCall.Callee, out var methodSignature) &&
                     !methodSignature.ReturnType.IsAnyOrUnknown:
                type = methodSignature.ReturnType;
                return true;

            case IrIntrinsicCallExpression intrinsic:
                type = intrinsic.ReturnType;
                return !type.IsAnyOrUnknown;

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
            if (actual.Kind == IrTypeKind.Structural)
            {
                foreach (var expectedField in expected.StructuralFields)
                {
                    var actualField = actual.StructuralFields
                        .FirstOrDefault(field => string.Equals(field.Name, expectedField.Name, StringComparison.Ordinal));

                    if (actualField == null)
                    {
                        if (!expectedField.Optional)
                            return false;

                        continue;
                    }

                    if (!IsTypeAssignable(expectedField.Type, actualField.Type))
                        return false;
                }

                return true;
            }

            return actual.Kind == IrTypeKind.Primitive && IsObjectTypeName(actual.Name);
        }

        if (expected.Kind != IrTypeKind.Primitive || actual.Kind != IrTypeKind.Primitive)
        {
            return false;
        }

        if (string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(expected.Name, "object", StringComparison.Ordinal) && IsObjectTypeName(actual.Name))
            return true;

        if (string.Equals(expected.Name, "float", StringComparison.Ordinal) &&
            string.Equals(actual.Name, "int", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsObjectTypeName(string? name) =>
        name != null && name.ToLowerInvariant() is not
            ("string" or "int" or "float" or "bool" or "array" or "any" or "void");

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

    private sealed record NamedCallable(string EmittedName, FunctionDeclarationNode Node, string DisplayName);

    private sealed record ReturnInference(IrTypeRef Type, bool Conflict);
}
