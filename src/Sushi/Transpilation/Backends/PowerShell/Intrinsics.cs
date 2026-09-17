using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

namespace Sushi.Transpilation.Backends.PowerShell;

public sealed partial class PowerShellEmitter
{
    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.StringTrim => $"$null = {EmitStringTrim(call.Arguments)}",
            IntrinsicId.StringLower => $"$null = {EmitStringLower(call.Arguments)}",
            IntrinsicId.StringUpper => $"$null = {EmitStringUpper(call.Arguments)}",
            IntrinsicId.StringLength => $"$null = {EmitStringLength(call.Arguments)}",
            IntrinsicId.StringSplit => $"$null = {EmitStringSplit(call.Arguments)}",
            IntrinsicId.StringContains => $"$null = {EmitStringContains(call.Arguments)}",
            IntrinsicId.StringStartsWith => $"$null = {EmitStringStartsWith(call.Arguments)}",
            IntrinsicId.StringEndsWith => $"$null = {EmitStringEndsWith(call.Arguments)}",
            IntrinsicId.StringReplace => $"$null = {EmitStringReplace(call.Arguments)}",
            IntrinsicId.StringIsMatch => $"$null = {EmitStringIsMatch(call.Arguments)}",
            IntrinsicId.StringMatch => $"$null = {EmitStringMatch(call.Arguments)}",
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.EnvUnset => $"Remove-Item -Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue",
            IntrinsicId.ProcessExit => $"exit {EmitValueExpression(call.Arguments[0])}",
            IntrinsicId.ProcessSleep => $"Start-Sleep -Milliseconds {Arg(call.Arguments, 0)}",
            IntrinsicId.ConsoleError => $"[Console]::Error.WriteLine([string]({Arg(call.Arguments, 0)}))",
            IntrinsicId.OsChdir => $"Set-Location -LiteralPath {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"$null = {EmitProcessRun(call.Arguments)}",
            IntrinsicId.ProcessPipeline => $"$null = {EmitProcessPipeline(call.Arguments)}",
            IntrinsicId.ProcessFail => $"$null = {EmitProcessFail(call.Arguments)}",
            IntrinsicId.ProcessRequireSuccess => $"$null = {EmitProcessRequireSuccess(call.Arguments)}",
            IntrinsicId.FsGlob => $"$null = {EmitFsGlob(call.Arguments)}",
            IntrinsicId.FsCreateDirectory => $"$null = {EmitFsCreateDirectory(call.Arguments)}",
            IntrinsicId.FsRemove => $"$null = {EmitFsRemove(call.Arguments)}",
            IntrinsicId.FsCopy => $"$null = {EmitFsCopy(call.Arguments)}",
            IntrinsicId.FsMove => $"$null = {EmitFsMove(call.Arguments)}",
            IntrinsicId.ArchiveZip => $"$null = {EmitArchiveZip(call.Arguments)}",
            IntrinsicId.ArchiveUnzip => $"$null = {EmitArchiveUnzip(call.Arguments)}",
            IntrinsicId.HttpGet => $"$null = {EmitHttpGet(call.Arguments)}",
            IntrinsicId.HttpPost => $"$null = {EmitHttpPost(call.Arguments)}",
            IntrinsicId.HttpDownload => $"$null = {EmitHttpDownload(call.Arguments)}",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in PowerShell", "$null")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.StringTrim => EmitStringTrim(call.Arguments),
            IntrinsicId.StringLower => EmitStringLower(call.Arguments),
            IntrinsicId.StringUpper => EmitStringUpper(call.Arguments),
            IntrinsicId.StringLength => EmitStringLength(call.Arguments),
            IntrinsicId.StringSplit => EmitStringSplit(call.Arguments),
            IntrinsicId.StringContains => EmitStringContains(call.Arguments),
            IntrinsicId.StringStartsWith => EmitStringStartsWith(call.Arguments),
            IntrinsicId.StringEndsWith => EmitStringEndsWith(call.Arguments),
            IntrinsicId.StringReplace => EmitStringReplace(call.Arguments),
            IntrinsicId.StringIsMatch => EmitStringIsMatch(call.Arguments),
            IntrinsicId.StringMatch => EmitStringMatch(call.Arguments),
            IntrinsicId.IoReadText => $"(Get-Content -Raw -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.FsIsFile => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)} -PathType Leaf)",
            IntrinsicId.FsIsDirectory => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)} -PathType Container)",
            IntrinsicId.FsSize => $"([int64](Get-Item -LiteralPath {Arg(call.Arguments, 0)}).Length)",
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Parent)",
            IntrinsicId.PathBasename => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Leaf)",
            IntrinsicId.PathExtension => $"([IO.Path]::GetExtension([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.PathStem => $"([IO.Path]::GetFileNameWithoutExtension([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.EnvHas => $"(Test-Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.ProcessArgs => "$args",
            IntrinsicId.ProcessWhich => "$($commandInfo = Get-Command -Name ([string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue; if ($null -eq $commandInfo) {{ $null }} else {{ $commandInfo.Source }})",
            IntrinsicId.ConsoleReadLine => "([Console]::ReadLine())",
            IntrinsicId.OsCwd => "((Get-Location).Path)",
            IntrinsicId.IoWriteText => $"({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"({EmitEnvSet(call.Arguments)})",
            IntrinsicId.EnvUnset => $"(Remove-Item -Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue)",
            IntrinsicId.ProcessExit => $"(exit {EmitValueExpression(call.Arguments[0])})",
            IntrinsicId.ProcessSleep => $"(Start-Sleep -Milliseconds {Arg(call.Arguments, 0)})",
            IntrinsicId.ConsoleError => $"([Console]::Error.WriteLine([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.OsChdir => $"(Set-Location -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => EmitProcessRun(call.Arguments),
            IntrinsicId.ProcessPipeline => EmitProcessPipeline(call.Arguments),
            IntrinsicId.ProcessFail => EmitProcessFail(call.Arguments),
            IntrinsicId.ProcessRequireSuccess => EmitProcessRequireSuccess(call.Arguments),
            IntrinsicId.FsGlob => EmitFsGlob(call.Arguments),
            IntrinsicId.FsCreateDirectory => EmitFsCreateDirectory(call.Arguments),
            IntrinsicId.FsRemove => EmitFsRemove(call.Arguments),
            IntrinsicId.FsCopy => EmitFsCopy(call.Arguments),
            IntrinsicId.FsMove => EmitFsMove(call.Arguments),
            IntrinsicId.ArchiveZip => EmitArchiveZip(call.Arguments),
            IntrinsicId.ArchiveUnzip => EmitArchiveUnzip(call.Arguments),
            IntrinsicId.HttpGet => EmitHttpGet(call.Arguments),
            IntrinsicId.HttpPost => EmitHttpPost(call.Arguments),
            IntrinsicId.HttpDownload => EmitHttpDownload(call.Arguments),
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in PowerShell: {call.CanonicalName}", "$null")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        // println is part of the script's output stream; Write-Output preserves
        // that composability while print intentionally remains terminal-style.
        return newline ? $"Write-Output {value}" : $"Write-Host -NoNewline {value}";
    }

    private string EmitStringTrim(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).Trim()";
    }

    private string EmitStringArgument(IrExpression expression)
    {
        var value = EmitValueExpression(expression);
        return expression switch
        {
            IrLiteralExpression { Value: string } => value,
            IrIdentifierExpression { StaticType.Name: "string" } => value,
            _ => $"[string]({value})"
        };
    }

    private string EmitStringLower(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).ToLowerInvariant()";
    }

    private string EmitStringUpper(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).ToUpperInvariant()";
    }

    private string EmitStringLength(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).Length";
    }

    private string EmitStringSplit(IReadOnlyList<IrExpression> arguments)
    {
        return $"@(({EmitStringArgument(arguments[0])}).Split({EmitStringArgument(arguments[1])}, [int]({Arg(arguments, 2)})))";
    }

    private string EmitStringContains(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).Contains({EmitStringArgument(arguments[1])})";
    }

    private string EmitStringStartsWith(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).StartsWith({EmitStringArgument(arguments[1])})";
    }

    private string EmitStringEndsWith(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).EndsWith({EmitStringArgument(arguments[1])})";
    }

    private string EmitStringReplace(IReadOnlyList<IrExpression> arguments)
    {
        return $"({EmitStringArgument(arguments[0])}).Replace({EmitStringArgument(arguments[1])}, {EmitStringArgument(arguments[2])})";
    }

    private string EmitStringIsMatch(IReadOnlyList<IrExpression> arguments)
    {
        return $"[regex]::IsMatch({EmitStringArgument(arguments[0])}, {EmitStringArgument(arguments[1])})";
    }

    private string EmitStringMatch(IReadOnlyList<IrExpression> arguments)
    {
        return $"$($m=[regex]::Match([string]({Arg(arguments, 0)}), [string]({Arg(arguments, 1)})); [pscustomobject]@{{ ok=$m.Success; value=$m.Value; index=$m.Index; groups=@($m.Groups | ForEach-Object Value) }})";
    }

    private string EmitIoWriteText(IReadOnlyList<IrExpression> arguments)
    {
        var path = Arg(arguments, 0);
        var text = Arg(arguments, 1);
        var append = Arg(arguments, 2);
        return "$__sushi_path = [string](" + path + "); " +
               "$__sushi_dir = Split-Path -Path $__sushi_path -Parent; " +
               "if ($__sushi_dir -and -not (Test-Path -LiteralPath $__sushi_dir)) { New-Item -ItemType Directory -Path $__sushi_dir -Force | Out-Null }; " +
               "if ([bool]" + append + ") { Add-Content -LiteralPath $__sushi_path -Value " + text + " } else { Set-Content -LiteralPath $__sushi_path -Value " + text + " }";
    }

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return $"Set-Item -Path (\"Env:\" + [string]({name})) -Value {value}";
    }

    private string EmitEnvGet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var fallback = Arg(arguments, 1);
        return $"$($n=[string]({name}); if (Test-Path (\"Env:\" + $n)) {{ (Get-Item (\"Env:\" + $n)).Value }} else {{ {fallback} }})";
    }

    private string EmitPathJoin(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.Count == 0)
        {
            return "''";
        }

        if (arguments.Count == 1)
        {
            return Arg(arguments, 0);
        }

        var args = string.Join(", ", arguments.Select(EmitValueExpression));
        return $"$($parts=@({args}); $j=$parts[0]; for($i=1; $i -lt $parts.Count; $i++){{ $j = Join-Path -Path $j -ChildPath $parts[$i] }}; $j)";
    }

    private string EmitProcessRun(IReadOnlyList<IrExpression> arguments)
    {
        return EmitAggregateIntrinsicFallback(IntrinsicId.ProcessRun);
    }

    private string EmitProcessPipeline(IReadOnlyList<IrExpression> arguments)
    {
        return EmitAggregateIntrinsicFallback(IntrinsicId.ProcessPipeline);
    }

    private string EmitProcessFail(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.FirstOrDefault() is IrIdentifierExpression identifier)
            return $"(-not [bool]${SanitizeName(identifier.Name)}.ok)";
        return EmitAggregateIntrinsicFallback(IntrinsicId.ProcessFail);
    }

    private string EmitProcessRequireSuccess(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.FirstOrDefault() is IrIdentifierExpression identifier)
        {
            var name = SanitizeName(identifier.Name);
            return $"$(if (-not [bool]${name}.ok) {{ exit [int]${name}.code }}; ${name})";
        }
        return EmitAggregateIntrinsicFallback(IntrinsicId.ProcessRequireSuccess);
    }

    private string EmitFsGlob(IReadOnlyList<IrExpression> arguments)
    {
        return EmitAggregateIntrinsicFallback(IntrinsicId.FsGlob);
    }

    private string EmitExplicitFsGlob(IReadOnlyList<IrExpression> arguments) =>
        $"(__sushi_fs_glob -pattern ([string]({Arg(arguments, 0)})) -cwd {Arg(arguments, 1)})";

    private string EmitFsSize(IReadOnlyList<IrExpression> arguments) =>
        $"([int64]$(if ((Get-Item -LiteralPath {Arg(arguments, 0)}).PSIsContainer) {{ throw 'std.fs.size: regular file required' }} else {{ (Get-Item -LiteralPath {Arg(arguments, 0)}).Length }}))";

    private string EmitFsCreateDirectory(IReadOnlyList<IrExpression> arguments) =>
        $"(New-Item -ItemType Directory -Force -Path {Arg(arguments, 0)})";

    private string EmitFsRemove(IReadOnlyList<IrExpression> arguments) =>
        $"$(if ([bool]({Arg(arguments, 1)})) {{ Remove-Item -LiteralPath {Arg(arguments, 0)} -Force -Recurse }} else {{ Remove-Item -LiteralPath {Arg(arguments, 0)} -Force }})";

    private string EmitFsCopy(IReadOnlyList<IrExpression> arguments) =>
        $"$(if ([bool]({Arg(arguments, 2)})) {{ Copy-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Recurse -Force }} else {{ Copy-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Force }})";

    private string EmitFsMove(IReadOnlyList<IrExpression> arguments) =>
        $"(Move-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Force)";

    private string EmitArchiveZip(IReadOnlyList<IrExpression> arguments) =>
        $"(Compress-Archive -Path {Arg(arguments, 0)} -DestinationPath {Arg(arguments, 1)} -Force)";

    private string EmitArchiveUnzip(IReadOnlyList<IrExpression> arguments) =>
        $"(Expand-Archive -LiteralPath {Arg(arguments, 0)} -DestinationPath {Arg(arguments, 1)} -Force)";

    private string EmitHttpGet(IReadOnlyList<IrExpression> arguments)
    {
        return EmitAggregateIntrinsicFallback(IntrinsicId.HttpGet);
    }

    private string EmitHttpPost(IReadOnlyList<IrExpression> arguments)
    {
        return EmitAggregateIntrinsicFallback(IntrinsicId.HttpPost);
    }

    private string EmitHttpDownload(IReadOnlyList<IrExpression> arguments) =>
        $"(Invoke-WebRequest -UseBasicParsing -Uri {Arg(arguments, 0)} -OutFile {Arg(arguments, 1)})";

    private string EmitAggregateIntrinsicFallback(IntrinsicId id) =>
        _context.ErrorAndReturn(AmbiguousShapeCode,
            $"std.{id} must be assigned to a variable before use on PowerShell targets.", "$null");

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "$null";
    }
}
