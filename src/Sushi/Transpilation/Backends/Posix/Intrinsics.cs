using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

namespace Sushi.Transpilation.Backends.Posix;

public sealed partial class PosixEmitter
{
    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.StringTrim => $"{EmitStringTrimInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringLower => $"{EmitStringLowerInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringUpper => $"{EmitStringUpperInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringLength => $"{EmitStringLengthInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringSplit => $"{EmitStringSplitInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringContains => $"{EmitStringContainsInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringStartsWith => $"{EmitStringStartsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringEndsWith => $"{EmitStringEndsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringReplace => $"{EmitStringReplaceInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringIsMatch => $"{EmitStringIsMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringMatch => $"{EmitStringMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.EnvUnset => EmitEnvUnset(call.Arguments),
            IntrinsicId.ProcessExit => EmitProcessExit(call.Arguments),
            IntrinsicId.ProcessSleep => EmitProcessSleep(call.Arguments),
            IntrinsicId.ConsoleError => EmitConsoleError(call.Arguments),
            IntrinsicId.OsChdir => $"cd -- {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"{EmitProcessRunInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessPipeline => $"{EmitProcessPipelineInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessFail => $"{EmitProcessFailInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessRequireSuccess => $"{EmitProcessRequireSuccessInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsGlob => $"{EmitFsGlobInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsCreateDirectory => EmitFsCreateDirectory(call.Arguments),
            IntrinsicId.FsRemove => EmitFsRemove(call.Arguments),
            IntrinsicId.FsCopy => EmitFsCopy(call.Arguments),
            IntrinsicId.FsMove => EmitFsMove(call.Arguments),
            IntrinsicId.ArchiveZip => EmitArchiveZip(call.Arguments),
            IntrinsicId.ArchiveUnzip => EmitArchiveUnzip(call.Arguments),
            IntrinsicId.HttpGet => $"{EmitHttpGetInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpPost => $"{EmitHttpPostInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpDownload => EmitHttpDownload(call.Arguments),
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in Bash")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"$({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"$({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.StringTrim => $"\"$({EmitStringTrimInvocation(call.Arguments)})\"",
            IntrinsicId.StringLower => $"\"$({EmitStringLowerInvocation(call.Arguments)})\"",
            IntrinsicId.StringUpper => $"\"$({EmitStringUpperInvocation(call.Arguments)})\"",
            IntrinsicId.StringLength => EmitStringLengthValue(call.Arguments),
            IntrinsicId.StringSplit => $"\"$({EmitStringSplitInvocation(call.Arguments)})\"",
            IntrinsicId.StringContains => $"\"$({EmitStringContainsInvocation(call.Arguments)})\"",
            IntrinsicId.StringStartsWith => $"\"$({EmitStringStartsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringEndsWith => $"\"$({EmitStringEndsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringReplace => $"\"$({EmitStringReplaceInvocation(call.Arguments)})\"",
            IntrinsicId.StringIsMatch => $"\"$({EmitStringIsMatchInvocation(call.Arguments)})\"",
            IntrinsicId.StringMatch => $"\"$({EmitStringMatchInvocation(call.Arguments)})\"",
            IntrinsicId.IoReadText => $"$(cat -- {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"$([[ -e {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsIsFile => $"$([[ -f {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsIsDirectory => $"$([[ -d {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsSize => EmitFsSize(call.Arguments),
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"$(dirname -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathBasename => $"$(basename -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathExtension => EmitPathExtension(call.Arguments),
            IntrinsicId.PathStem => EmitPathStem(call.Arguments),
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.EnvHas => $"$(__sushi_env_name=$(printf '%s' {Arg(call.Arguments, 0)}); [[ -v $__sushi_env_name ]] && printf 'true' || printf 'false')",
            IntrinsicId.ProcessArgs => "\"$@\"",
            IntrinsicId.ProcessWhich => $"$(command -v -- {Arg(call.Arguments, 0)} 2>/dev/null || true)",
            IntrinsicId.ConsoleReadLine => "$(IFS= read -r __sushi_line; printf '%s' \"$__sushi_line\")",
            IntrinsicId.OsCwd => "$(pwd)",
            IntrinsicId.IoWriteText => $"$({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"$({EmitEnvSet(call.Arguments)})",
            IntrinsicId.EnvUnset => $"$({EmitEnvUnset(call.Arguments)})",
            IntrinsicId.ProcessExit => $"$({EmitProcessExit(call.Arguments)})",
            IntrinsicId.ProcessSleep => $"$({EmitProcessSleep(call.Arguments)})",
            IntrinsicId.ConsoleError => $"$({EmitConsoleError(call.Arguments)})",
            IntrinsicId.OsChdir => $"$(cd -- {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => $"\"$({EmitProcessRunInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessPipeline => $"\"$({EmitProcessPipelineInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessFail => $"\"$({EmitProcessFailInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessRequireSuccess => $"\"$({EmitProcessRequireSuccessInvocation(call.Arguments)})\"",
            IntrinsicId.FsGlob => $"\"$({EmitFsGlobInvocation(call.Arguments)})\"",
            IntrinsicId.FsCreateDirectory => $"$({EmitFsCreateDirectory(call.Arguments)})",
            IntrinsicId.FsRemove => $"$({EmitFsRemove(call.Arguments)})",
            IntrinsicId.FsCopy => $"$({EmitFsCopy(call.Arguments)})",
            IntrinsicId.FsMove => $"$({EmitFsMove(call.Arguments)})",
            IntrinsicId.ArchiveZip => $"$({EmitArchiveZip(call.Arguments)})",
            IntrinsicId.ArchiveUnzip => $"$({EmitArchiveUnzip(call.Arguments)})",
            IntrinsicId.HttpGet => $"\"$({EmitHttpGetInvocation(call.Arguments)})\"",
            IntrinsicId.HttpPost => $"\"$({EmitHttpPostInvocation(call.Arguments)})\"",
            IntrinsicId.HttpDownload => $"$({EmitHttpDownload(call.Arguments)})",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in Bash: {call.CanonicalName}")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        return newline
            ? $"printf '%s\\n' {value}"
            : $"printf '%s' {value}";
    }

    private string EmitStringTrimInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_trim {Arg(arguments, 0)}";
    }

    private string EmitStringLowerInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_lower {Arg(arguments, 0)}";
    }

    private string EmitStringUpperInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_upper {Arg(arguments, 0)}";
    }

    private string EmitStringLengthInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"printf '%s' {Arg(arguments, 0)} | wc -m";
    }

    private string EmitStringLengthValue(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.Count > 0 && arguments[0] is IrIdentifierExpression identifier)
            return $"${{#{SanitizeVariableName(identifier.Name)}}}";
        return $"$({EmitStringLengthInvocation(arguments)})";
    }

    private string EmitStringSplitInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_split {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringContainsInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_contains {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringStartsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_starts_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringEndsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_ends_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringReplaceInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_replace {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringIsMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_is_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitIoWriteText(IReadOnlyList<IrExpression> arguments)
    {
        var path = Arg(arguments, 0);
        var text = Arg(arguments, 1);
        var append = Arg(arguments, 2);
        return EmitIoWriteText(path, text, append);
    }

    private static string EmitIoWriteText(string path, string text, string append)
    {
        return "__sushi_path=$(printf '%s' " + path + "); " +
               "__sushi_dir=$(dirname -- \"$__sushi_path\"); " +
               "if [[ \"$__sushi_dir\" != \".\" && ! -d \"$__sushi_dir\" ]]; then mkdir -p -- \"$__sushi_dir\"; fi; " +
               "if [[ " + append + " == 'true' ]]; then printf '%s' " + text + " >> \"$__sushi_path\"; else printf '%s' " + text + " > \"$__sushi_path\"; fi";
    }

    private string EmitFsCreateDirectory(IReadOnlyList<IrExpression> arguments) =>
        $"mkdir -p -- {Arg(arguments, 0)}";

    private string EmitFsSize(IReadOnlyList<IrExpression> arguments) =>
        _context.TargetProfile.Platform == TargetPlatform.Macos
            ? $"$(if [[ -f {Arg(arguments, 0)} ]]; then stat -f '%z' -- {Arg(arguments, 0)}; else printf 'std.fs.size: regular file required\\n' >&2; exit 1; fi)"
            : $"$(if [[ -f {Arg(arguments, 0)} ]]; then stat -c '%s' -- {Arg(arguments, 0)}; else printf 'std.fs.size: regular file required\\n' >&2; exit 1; fi)";

    private string EmitFsRemove(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 1)} == 'true' ]]; then rm -rf -- {Arg(arguments, 0)}; else rm -f -- {Arg(arguments, 0)}; fi";

    private string EmitFsCopy(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 2)} == 'true' ]]; then cp -R -- {Arg(arguments, 0)} {Arg(arguments, 1)}; else cp -- {Arg(arguments, 0)} {Arg(arguments, 1)}; fi";

    private string EmitFsMove(IReadOnlyList<IrExpression> arguments) =>
        $"mv -f -- {Arg(arguments, 0)} {Arg(arguments, 1)}";

    private string EmitArchiveZip(IReadOnlyList<IrExpression> arguments) =>
        $"zip -qr {Arg(arguments, 1)} {Arg(arguments, 0)}";

    private string EmitArchiveUnzip(IReadOnlyList<IrExpression> arguments) =>
        $"unzip -oq {Arg(arguments, 0)} -d {Arg(arguments, 1)}";

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return EmitEnvSet(name, value);
    }

    private static string EmitEnvSet(string name, string value)
    {
        return $"__sushi_env_name=$(printf '%s' {name}); __sushi_env_value=$(printf '%s' {value}); export \"$__sushi_env_name=$__sushi_env_value\"";
    }

    private string EmitEnvUnset(IReadOnlyList<IrExpression> arguments) =>
        $"__sushi_env_name=$(printf '%s' {Arg(arguments, 0)}); unset \"$__sushi_env_name\"";

    private string EmitProcessSleep(IReadOnlyList<IrExpression> arguments) =>
        $"sleep \"$(({Arg(arguments, 0)} / 1000)).$(({Arg(arguments, 0)} % 1000))\"";

    private string EmitConsoleError(IReadOnlyList<IrExpression> arguments) =>
        $"printf '%s\\n' {Arg(arguments, 0)} >&2";

    private string EmitPathExtension(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '.%s' \"${{__sushi_base##*.}}\"; fi)";

    private string EmitPathStem(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '%s' \"${{__sushi_base%.*}}\"; else printf '%s' \"$__sushi_base\"; fi)";

    private string EmitProcessExit(IReadOnlyList<IrExpression> arguments)
    {
        return $"exit {EmitArithmeticExpression(arguments[0])}";
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

        var first = Arg(arguments, 0);
        var rest = arguments.Skip(1).Select(argument => $"printf '/%s' {EmitValueExpression(argument)}");
        var commands = string.Join("; ", new[] { $"printf '%s' {first}" }.Concat(rest));
        return $"$({commands})";
    }

    private string EmitEnvGet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var fallback = Arg(arguments, 1);
        return "$(__sushi_env_name=$(printf '%s' " + name + "); " +
               "__sushi_env_value=$(printenv \"$__sushi_env_name\" 2>/dev/null || true); " +
               "if printenv \"$__sushi_env_name\" >/dev/null 2>&1; then printf '%s' \"$__sushi_env_value\"; else printf '%s' " + fallback + "; fi)";
    }

    private string EmitProcessRunInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_run " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)} " +
               $"{Arg(arguments, 7)}";
    }

    private string EmitProcessPipelineInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_pipeline " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)}";
    }

    private string EmitProcessFailInvocation(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.FirstOrDefault() is IrIdentifierExpression identifier)
        {
            var name = SanitizeVariableName(identifier.Name);
            return $"if [[ \"${{{name}_ok-}}\" != true ]]; then exit \"${{{name}_code:-1}}\"; fi";
        }
        return $"__sushi_process_fail {Arg(arguments, 0)}";
    }

    private string EmitProcessRequireSuccessInvocation(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.FirstOrDefault() is IrIdentifierExpression identifier)
        {
            var name = SanitizeVariableName(identifier.Name);
            return $"if [[ \"${{{name}_ok-}}\" != true ]]; then exit \"${{{name}_code:-1}}\"; fi";
        }
        return $"__sushi_process_require_success {Arg(arguments, 0)}";
    }

    private string EmitFsGlobInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_fs_glob {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpGetInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_get {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpPostInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_post {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)} {Arg(arguments, 3)}";
    }

    private string EmitHttpDownload(IReadOnlyList<IrExpression> arguments) =>
        $"curl -fsSL -o {Arg(arguments, 1)} -- {Arg(arguments, 0)}";

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "''";
    }
}
