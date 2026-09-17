using System.Globalization;
using System.Linq;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

namespace Sushi.Transpilation.Backends.PowerShell;

public sealed partial class PowerShellEmitter
{
    private void EmitNativeEnum(IrEnumDeclarationStatement declaration)
    {
        var enumName = _nativeEnumNames[declaration.Name];
        WriteLine($"enum {enumName} {{");
        _indent++;
        foreach (var value in declaration.Values)
        {
            var valueName = SanitizeMemberName(value.Name);
            WriteLine($"{valueName} = {value.Value}");
            _nativeEnumValues[$"{declaration.Name}_{value.Name}"] = (enumName, valueName, value.Ordinal);
        }
        _indent--;
        WriteLine("}");
    }

    private static IEnumerable<IrClassDeclarationStatement> CollectClasses(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrClassDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectClasses(block.Statements)) yield return nestedDeclaration;
        }
    }

    private static IEnumerable<IrEnumDeclarationStatement> CollectEnums(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrEnumDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectEnums(block.Statements)) yield return nestedDeclaration;
        }
    }

    private static IEnumerable<IrRichEnumDeclarationStatement> CollectRichEnums(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrRichEnumDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectRichEnums(block.Statements)) yield return nestedDeclaration;
        }
    }

    private void EmitRichEnum(IrRichEnumDeclarationStatement declaration)
    {
        var typeName = _names.Source(TargetNameKind.Type, declaration.Name);
        var fields = declaration.Values.SelectMany(value => value.Properties)
            .Where(property => !property.Name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal) &&
                               property.Name != NativeObjectMetadata.EnumValue)
            .Select(property => property.Name).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name == NativeObjectMetadata.EnumName ? 1 :
                             name == NativeObjectMetadata.EnumOrdinal ? 2 : 0)
            .ToList();
        foreach (var value in declaration.Values)
            _richEnumValues[$"{declaration.Name}_{value.Name}"] = (typeName, SanitizeMemberName(value.Name));
        WriteLine($"class {typeName} {{");
        _indent++;
        foreach (var field in fields) WriteLine($"[object] ${SanitizeMemberName(field)}");
        var parameters = string.Join(", ", fields.Select(field => $"[object]${SanitizeName(field)}"));
        WriteLine($"{typeName}({parameters}) {{");
        _indent++;
        foreach (var field in fields)
        {
            if (declaration.ConstructorBody != null && declaration.ConstructorParameters.Any(parameter => parameter.Name == field) &&
                ConstructorAssignsParameter(declaration.ConstructorBody, field, field))
                continue;
            WriteLine($"$this.{SanitizeMemberName(field)} = ${SanitizeName(field)}");
        }
        if (declaration.ConstructorBody != null)
            EmitClassBody(declaration.ConstructorBody, IrTypeRef.Any);
        _indent--;
        WriteLine("}");
        foreach (var method in declaration.Methods.Concat(declaration.Adapters))
        {
            var returnType = ContainsValueReturn(method.Body) ? EmitPowerShellType(method.ReturnType, declaration.Name) : "[void]";
            var methodParameters = string.Join(", ", method.Parameters.Select(parameter => EmitPowerShellParameter(parameter, declaration.Name)));
            WriteLine($"{returnType} {SanitizeMemberName(NativeMethodName(method.Name))}({methodParameters}) {{");
            _indent++;
            EmitClassBody(method.Body, method.ReturnType);
            _indent--;
            WriteLine("}");
        }
        foreach (var value in declaration.Values)
        {
            var valuesByField = value.Properties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var arguments = string.Join(", ", fields.Select(field =>
            {
                var parameterIndex = declaration.ConstructorParameters.FindIndex(parameter => parameter.Name == field);
                if (parameterIndex >= 0 && parameterIndex < value.ConstructorArguments.Count)
                    return EmitValueExpression(value.ConstructorArguments[parameterIndex]);
                return valuesByField.TryGetValue(field, out var expression) ? EmitValueExpression(expression) : "$null";
            }));
            var valueName = SanitizeMemberName(value.Name);
            WriteLine($"static [{typeName}] ${valueName} = [{typeName}]::new({arguments})");
            // Explicit enum constructors are only the portable construction
            // path. Static native instances above replace them on PowerShell.
        }
        _indent--;
        WriteLine("}");
    }

    private IEnumerable<IrClassDeclarationStatement> OrderClasses(IEnumerable<IrClassDeclarationStatement> classes)
    {
        var remaining = classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(c => ReferencedClassTypes(c).All(type => !remaining.ContainsKey(type) || type == c.Name))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            // Cycles use [object] on the relevant member annotations, so any
            // deterministic order is valid once no acyclic declaration remains.
            if (ready.Count == 0) ready.Add(remaining.Values.OrderBy(c => c.Name, StringComparer.Ordinal).First());
            foreach (var declaration in ready)
            {
                remaining.Remove(declaration.Name);
                emitted.Add(declaration.Name);
                yield return declaration;
            }
        }
    }

    private static IEnumerable<string> ReferencedClassTypes(IrClassDeclarationStatement declaration) =>
        declaration.Fields.Select(field => field.Type)
            .Concat(declaration.ConstructorParameters.Select(parameter => parameter.DeclaredType))
            .Concat(declaration.Methods.Concat(declaration.Adapters).SelectMany(method =>
                method.Parameters.Select(parameter => parameter.DeclaredType).Append(method.ReturnType)))
            .Where(type => type.Kind == IrTypeKind.Primitive && type.Name is not null)
            .Select(type => type.Name!);

    private void EmitNativeClass(IrClassDeclarationStatement declaration)
    {
        var className = _nativeClassNames[declaration.Name];
        WriteLine($"class {className} {{");
        _indent++;
        foreach (var field in declaration.Fields)
            WriteLine($"{EmitPowerShellType(field.Type, declaration.Name)} ${SanitizeMemberName(field.Name)}");

        var constructorParameters = string.Join(", ", declaration.ConstructorParameters.Select(p => EmitPowerShellParameter(p, declaration.Name)));
        WriteLine($"{className}({constructorParameters}) {{");
        _indent++;
        foreach (var field in declaration.Fields)
        {
            var matchingParameter = declaration.ConstructorParameters.FirstOrDefault(p => p.Name == field.Name);
            if (matchingParameter != null && ConstructorAssignsParameter(declaration.ConstructorBody, field.Name, matchingParameter.Name))
                continue;
            var value = matchingParameter != null
                ? "$" + SanitizeName(matchingParameter.Name)
                : field.Initializer != null ? EmitValueExpression(field.Initializer) : "$null";
            WriteLine($"$this.{SanitizeMemberName(field.Name)} = {value}");
        }
        EmitClassBody(declaration.ConstructorBody, IrTypeRef.Any);
        _indent--;
        WriteLine("}");

        foreach (var method in declaration.Methods.Concat(declaration.Adapters))
        {
            var returnType = ContainsValueReturn(method.Body)
                ? EmitPowerShellType(method.ReturnType, declaration.Name)
                : "[void]";
            var parameters = string.Join(", ", method.Parameters.Select(p => EmitPowerShellParameter(p, declaration.Name)));
            WriteLine($"{returnType} {SanitizeMemberName(NativeMethodName(method.Name))}({parameters}) {{");
            _indent++;
            EmitClassBody(method.Body, method.ReturnType);
            _indent--;
            WriteLine("}");
        }
        _indent--;
        WriteLine("}");
    }

    private static bool ConstructorAssignsParameter(IrBlockStatement body, string fieldName, string parameterName) =>
        body.Statements.Any(statement => statement is IrExpressionStatement
        {
            Expression: IrMemberAssignmentExpression
            {
                Operator: "=",
                Target: IrIdentifierExpression { Name: "this" },
                MemberName: var assignedField,
                Value: IrIdentifierExpression { Name: var assignedParameter }
            }
        } && assignedField == fieldName && assignedParameter == parameterName);

    private void EmitClassBody(IrBlockStatement body, IrTypeRef returnType)
    {
        var previousName = _currentFunctionName;
        var previousReturn = _currentFunctionReturnType;
        var previousIntegers = _knownIntegerVariables;
        _currentFunctionName = null;
        _currentFunctionReturnType = returnType;
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        EmitStatement(body);
        _currentFunctionName = previousName;
        _currentFunctionReturnType = previousReturn;
        _knownIntegerVariables = previousIntegers;
    }

    private static bool ContainsValueReturn(IrStatement statement) => statement switch
    {
        IrReturnStatement { Expression: not null } => true,
        IrBlockStatement block => block.Statements.Any(ContainsValueReturn),
        IrIfStatement conditional => ContainsValueReturn(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && ContainsValueReturn(conditional.ElseBlock)),
        IrWhileStatement loop => ContainsValueReturn(loop.Body),
        IrForStatement loop => ContainsValueReturn(loop.Body),
        IrDoWhileStatement loop => ContainsValueReturn(loop.Body),
        _ => false
    };

    private static string NativeMethodName(string name) => name.ToLowerInvariant() switch
    {
        "string" => "ToString",
        // Sushi `int` is a 64-bit integer across targets. Keep index casts below
        // as [int] where the PowerShell API specifically requires Int32 indices.
        "int" => "ToInt64",
        "float" => "ToDouble",
        "bool" => "ToBoolean",
        "array" => "ToArray",
        "object" => "ToObject",
        _ => name
    };
}
