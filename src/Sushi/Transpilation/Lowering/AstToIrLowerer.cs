namespace Sushi.Transpilation.Lowering;

using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class AstToIrLowerer
{
    private const string UnsupportedSyntaxCode = "SUSHI1001";
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

    private readonly List<Diagnostic> _diagnostics = new();
    private readonly IntrinsicRegistry _intrinsicRegistry = IntrinsicRegistry.CreateDefault();
    private readonly Dictionary<string, IrFunctionSignature> _functionSignatures = new();

    private string _sourcePath = "";
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public IrProgram Lower(ProgramNode program, string sourcePath)
    {
        _sourcePath = sourcePath;
        _functionSignatures.Clear();
        CollectFunctionSignatures(program);

        var output = new IrProgram();

        foreach (var declaration in program.Declarations)
        {
            var lowered = LowerTopLevel(declaration);
            if (lowered != null)
            {
                output.Statements.Add(lowered);
            }
        }

        return output;
    }

    private IrStatement? LowerTopLevel(AstNode node)
    {
        return node switch
        {
            BoxDeclarationNode => null,
            UseDeclarationNode => null,
            FunctionDeclarationNode function => LowerFunction(function),
            StatementNode statement => LowerStatement(statement),
            _ => UnsupportedStatement(node, "Top-level declaration is not yet supported in transpilation")
        };
    }

    private IrFunctionDeclarationStatement? LowerFunction(FunctionDeclarationNode node)
    {
        var signature = _functionSignatures.TryGetValue(node.Name, out var existingSignature)
            ? existingSignature
            : BuildFunctionSignature(node);

        var previousFunctionName = _currentFunctionName;
        var previousReturnType = _currentFunctionReturnType;
        _currentFunctionName = node.Name;
        _currentFunctionReturnType = signature.ReturnType;

        var body = node.Body switch
        {
            BlockStatementNode block => LowerBlock(block),
            StatementNode statement => LowerSingleStatementBlock(statement),
            _ => null
        };

        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;

        if (body == null)
        {
            AddDiagnostic(UnsupportedSyntaxCode, "Function body could not be lowered", node.Line, node.Column);
            return null;
        }

        return new IrFunctionDeclarationStatement(node.Name, signature.Parameters, body, signature.ReturnType);
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
                return new IrVariableDeclarationStatement(
                    declaration.Name,
                    declaration.Initializer != null ? LowerExpression(declaration.Initializer) : null);

            case ExpressionStatementNode expressionStatement:
                return new IrExpressionStatement(LowerExpression(expressionStatement.Expression));

            case IfStatementNode ifStatement:
                return new IrIfStatement(
                    LowerExpression(ifStatement.Condition),
                    StatementToBlock(ifStatement.ThenBranch),
                    ifStatement.ElseBranch != null ? StatementToBlock(ifStatement.ElseBranch) : null);

            case WhileStatementNode whileStatement:
                return new IrWhileStatement(
                    LowerExpression(whileStatement.Condition),
                    StatementToBlock(whileStatement.Body));

            case ForStatementNode forStatement:
                return new IrForStatement(
                    forStatement.Initializer != null ? LowerStatement(forStatement.Initializer) : null,
                    forStatement.Condition != null ? LowerExpression(forStatement.Condition) : null,
                    forStatement.Increment != null ? LowerExpression(forStatement.Increment) : null,
                    StatementToBlock(forStatement.Body));

            case ReturnStatementNode returnStatement:
            {
                var expression = returnStatement.Expression != null ? LowerExpression(returnStatement.Expression) : null;
                ValidateReturnType(expression, returnStatement.Line, returnStatement.Column);
                return new IrReturnStatement(expression);
            }

            case BreakStatementNode:
                return new IrBreakStatement();

            case ContinueStatementNode:
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

    private IrExpression LowerExpression(ExpressionNode node)
    {
        return node switch
        {
            LiteralExpressionNode literal => new IrLiteralExpression(literal.Value),
            IdentifierExpressionNode identifier => new IrIdentifierExpression(identifier.Name),
            ParenthesizedExpressionNode parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionNode unary => new IrUnaryExpression(unary.Operator, LowerExpression(unary.Operand), unary.IsPrefix),
            BinaryExpressionNode binary => LowerBinary(binary),
            ArrayLiteralExpressionNode array => new IrArrayLiteralExpression(array.Elements.Select(LowerExpression)),
            ObjectLiteralExpressionNode obj => LowerObjectLiteral(obj),
            MemberAccessExpressionNode member => new IrMemberAccessExpression(LowerExpression(member.Object), member.MemberName),
            IndexExpressionNode index => new IrIndexExpression(LowerExpression(index.Array), LowerExpression(index.Index)),
            CallExpressionNode call => LowerCall(call),
            _ => UnsupportedExpression(node)
        };
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
            if (node.Left is not IdentifierExpressionNode identifier)
            {
                AddDiagnostic(
                    InvalidAssignmentTargetCode,
                    $"Assignment target for operator '{node.Operator}' must be an identifier",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }

            return new IrAssignmentExpression(
                new IrIdentifierExpression(identifier.Name),
                node.Operator,
                LowerExpression(node.Right));
        }

        return new IrBinaryExpression(
            LowerExpression(node.Left),
            node.Operator,
            LowerExpression(node.Right));
    }

    private IrExpression LowerCall(CallExpressionNode node)
    {
        var intrinsicArguments = node.Arguments
            .Select(argument => new IntrinsicCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
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

                if (!binding.Success)
                {
                    return new IrLiteralExpression(null);
                }

                return new IrIntrinsicCallExpression(
                    signature.CanonicalName,
                    signature.Id,
                    binding.OrderedArguments);
            }

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

        if (node.Callee is not IdentifierExpressionNode callee)
        {
            AddDiagnostic(
                UnsupportedCallCode,
                "Only identifier-based function calls are supported in Milestone 1 transpilation",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        var loweredArguments = node.Arguments
            .Select(argument => new IrCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
                argument.Line,
                argument.Column))
            .ToList();

        if (_functionSignatures.TryGetValue(callee.Name, out var functionSignature))
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
            return new IrCallExpression(callee.Name, binding.OrderedArguments);
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

        return new IrCallExpression(callee.Name, loweredArguments);
    }

    private void CollectFunctionSignatures(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            if (declaration is not FunctionDeclarationNode function)
            {
                continue;
            }

            _functionSignatures[function.Name] = BuildFunctionSignature(function);
        }
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
            parameters.Add(new IrFunctionParameter(
                parameter.Name,
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
