#nullable enable

namespace Sushi.Tests.Transpilation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Sushi.Application;
using Sushi.Transpilation;
using Sushi.Transpilation.Intrinsics;
using Xunit;

/// <summary>
/// Executes every public standard-library declaration through generated Bash.
/// Each case owns a fresh directory, so file, environment, archive, process,
/// and HTTP behavior is verified without relying on repository state.
/// </summary>
public sealed class StandardLibraryEndToEndTests
{
    public static IEnumerable<object[]> Cases => AllCases.Select(testCase => new object[] { testCase });

    [Fact]
    public void EndToEndCases_CoverEveryStandardLibraryMember()
    {
        var catalogNames = StandardLibraryCatalog.CreateDefault().Functions.Select(function => function.Name).OrderBy(name => name);
        var testedNames = AllCases.Select(testCase => testCase.Name).OrderBy(name => name);

        Assert.Equal(catalogNames, testedNames);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void StandardLibraryMember_ExecutesEndToEnd(EndToEndCase testCase)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sushi-stdlib-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        HttpTestServer? server = null;
        try
        {
            testCase.Setup?.Invoke(root);
            if (testCase.NeedsHttpServer) server = new HttpTestServer();

            var source = testCase.Source.Replace("{{url}}", server?.Url ?? "");
            var transpiled = new Transpiler().Transpile(new TranspileRequest
            {
                SourceText = source,
                SourcePath = Path.Combine(root, "program.sushi"),
                TargetLanguage = TargetLanguage.Bash,
                TargetProfile = new TargetProfile(TargetLanguage.Bash, TargetPlatform.Linux)
            });
            Assert.True(transpiled.Success, $"{testCase.Name}: {string.Join("; ", transpiled.Diagnostics.Select(diagnostic => diagnostic.Message))}");

            var script = Path.Combine(root, "program.sh");
            File.WriteAllText(script, transpiled.EmittedCode);
            var startInfo = new ProcessStartInfo("bash")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(script);
            foreach (var argument in testCase.Arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            process.StandardInput.Write(testCase.Input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), $"{testCase.Name}: generated script timed out.");

            Assert.True(process.ExitCode == testCase.ExitCode,
                $"{testCase.Name}: expected exit code {testCase.ExitCode}, got {process.ExitCode}.\nstdout: {output}\nstderr: {error}\nscript:\n{transpiled.EmittedCode}");
            if (!testCase.IgnoreOutput) Assert.Equal(Normalize(testCase.Output), Normalize(output));
            Assert.Equal(Normalize(testCase.Error), Normalize(error));
            testCase.Verify?.Invoke(root, output, error);
        }
        finally
        {
            server?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static readonly IReadOnlyList<EndToEndCase> AllCases =
    [
        Case("print", "print(\"sushi\")", output: "sushi"),
        Case("println", "println(\"sushi\")", output: "sushi\n"),
        Case("string", "println(string(42))", output: "42\n"),

        Case("std.target.shell", "use std.target.shell\nprintln(shell())", output: "bash\n"),
        Case("std.target.platform", "use std.target.platform\nprintln(platform())", output: "linux\n"),

        Case("std.string.trim", "use std.string.trim\nprintln(trim(\"  sushi  \"))", output: "sushi\n"),
        Case("std.string.lower", "use std.string.lower\nprintln(lower(\"SuShI\"))", output: "sushi\n"),
        Case("std.string.upper", "use std.string.upper\nprintln(upper(\"SuShI\"))", output: "SUSHI\n"),
        Case("std.string.length", "use std.string.length\nprintln(length(\"sushi\"))", output: "5\n"),
        Case("std.string.split", "use std.string.split\nstring[] parts = split(\"su-shi\", \"-\")\nfor (string part : parts) { print(part) }", output: "sushi"),
        Case("std.string.contains", "use std.string.contains\nprintln(contains(\"sushi\", \"sh\"))", output: "true\n"),
        Case("std.string.startsWith", "use std.string.startsWith\nprintln(startsWith(\"sushi\", \"su\"))", output: "true\n"),
        Case("std.string.endsWith", "use std.string.endsWith\nprintln(endsWith(\"sushi\", \"shi\"))", output: "true\n"),
        Case("std.string.replace", "use std.string.replace\nprintln(replace(\"sushi sushi\", \"sushi\", \"ramen\"))", output: "ramen ramen\n"),
        Case("std.string.isMatch", "use std.string.isMatch\nprintln(isMatch(\"sushi-42\", \"[0-9]+\"))", output: "true\n"),
        Case("std.string.match", "use std.string.match\nvar result = match(\"sushi-42\", \"[0-9]+\")\nprintln(result.value)", output: "42\n"),

        Case("std.fs.writeText", "use std.fs.writeText\nwriteText(\"written.txt\", \"sushi\")", verify: (root, _, _) => Assert.Equal("sushi", File.ReadAllText(Path.Combine(root, "written.txt")))),
        Case("std.fs.readText", "use std.fs.readText\nprintln(readText(\"read.txt\"))", output: "sushi\n", setup: root => File.WriteAllText(Path.Combine(root, "read.txt"), "sushi")),
        Case("std.fs.exists", "use std.fs.exists\nprintln(exists(\"present.txt\"))", output: "true\n", setup: root => File.WriteAllText(Path.Combine(root, "present.txt"), "")),
        Case("std.fs.query", "use std.fs as fs\nvar root = \"glob\"\nvar pattern = \"*.sushi\"\nvar baseQuery = fs.query(root).recursive()\nvar query = baseQuery.matching(pattern)\nstring[] files = query.files()\nfor (string file : files) { println(file) }", output: "one.sushi\nnested/two.sushi\n", setup: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "glob", "nested"));
            File.WriteAllText(Path.Combine(root, "glob", "one.sushi"), "");
            File.WriteAllText(Path.Combine(root, "glob", "nested", "two.sushi"), "");
        }),
        Case("std.fs.size", "use std.fs.size\nprintln(size(\"size.txt\"))", output: "5\n", setup: root => File.WriteAllText(Path.Combine(root, "size.txt"), "sushi")),
        Case("std.fs.isFile", "use std.fs.isFile\nprintln(isFile(\"file.txt\"))", output: "true\n", setup: root => File.WriteAllText(Path.Combine(root, "file.txt"), "")),
        Case("std.fs.isDirectory", "use std.fs.isDirectory\nprintln(isDirectory(\"folder\"))", output: "true\n", setup: root => Directory.CreateDirectory(Path.Combine(root, "folder"))),
        Case("std.fs.createDirectory", "use std.fs.createDirectory\ncreateDirectory(\"made/nested\")", verify: (root, _, _) => Assert.True(Directory.Exists(Path.Combine(root, "made", "nested")))),
        Case("std.fs.remove", "use std.fs.remove\nremove(\"remove.txt\")", setup: root => File.WriteAllText(Path.Combine(root, "remove.txt"), ""), verify: (root, _, _) => Assert.False(File.Exists(Path.Combine(root, "remove.txt")))),
        Case("std.fs.copy", "use std.fs.copy\ncopy(\"source.txt\", \"copy.txt\")", setup: root => File.WriteAllText(Path.Combine(root, "source.txt"), "sushi"), verify: (root, _, _) => Assert.Equal("sushi", File.ReadAllText(Path.Combine(root, "copy.txt")))),
        Case("std.fs.move", "use std.fs.move\nmove(\"source.txt\", \"moved.txt\")", setup: root => File.WriteAllText(Path.Combine(root, "source.txt"), "sushi"), verify: (root, _, _) =>
        {
            Assert.False(File.Exists(Path.Combine(root, "source.txt")));
            Assert.Equal("sushi", File.ReadAllText(Path.Combine(root, "moved.txt")));
        }),

        Case("std.path.join", "use std.path.join\nprintln(join(\"alpha\", \"beta\", \"file.txt\"))", output: "alpha/beta/file.txt\n"),
        Case("std.path.dirname", "use std.path.dirname\nprintln(dirname(\"alpha/beta/file.txt\"))", output: "alpha/beta\n"),
        Case("std.path.basename", "use std.path.basename\nprintln(basename(\"alpha/beta/file.txt\"))", output: "file.txt\n"),
        Case("std.path.extension", "use std.path.extension\nprintln(extension(\"alpha/archive.tar.gz\"))", output: ".gz\n"),
        Case("std.path.stem", "use std.path.stem\nprintln(stem(\"alpha/archive.tar.gz\"))", output: "archive.tar\n"),

        Case("std.env.get", "use std.env.get\nprintln(get(\"SUSHI_E2E_MISSING\", \"fallback\"))", output: "fallback\n"),
        Case("std.env.set", "use std.env.set\nuse std.env.get\nset(\"SUSHI_E2E_VALUE\", \"sushi\")\nprintln(get(\"SUSHI_E2E_VALUE\"))", output: "sushi\n"),
        Case("std.env.has", "use std.env.has\nuse std.env.set\nset(\"SUSHI_E2E_VALUE\", \"sushi\")\nprintln(has(\"SUSHI_E2E_VALUE\"))", output: "true\n"),
        Case("std.env.unset", "use std.env.unset\nuse std.env.has\nunset(\"SUSHI_E2E_VALUE\")\nprintln(has(\"SUSHI_E2E_VALUE\"))", output: "false\n"),

        Case("std.process.args", "use std.process.args\nstring[] supplied = args()\nprintln(supplied[0])", output: "sushi-argument\n", arguments: ["sushi-argument"]),
        Case("std.process.exit", "use std.process.exit\nexit(7)", exitCode: 7),
        Case("std.process.which", "use std.process.which\nprintln(which(\"bash\"))", ignoreOutput: true, verify: (_, output, _) => Assert.Contains("bash", output)),
        Case("std.process.sleep", "use std.process.sleep\nsleep(1)\nprintln(\"awake\")", output: "awake\n"),
        Case("std.process.run", "use std.process.run\nvar result = run(\"printf\", [\"sushi\"])\nprintln(result.stdout)", output: "sushi\n"),
        Case("std.process.pipeline", "use std.process.pipeline\nvar stages = [{ command: \"printf\", args: [\"sushi\"] }, { command: \"tr\", args: [\"a-z\", \"A-Z\"] }]\nvar result = pipeline(stages)\nprintln(result.stdout)", output: "SUSHI\n"),
        Case("std.process.fail", "use std.process.fail\nuse std.process.run\nvar result = run(\"true\")\nfail(result)\nprintln(\"ok\")", output: "ok\n"),
        Case("std.process.requireSuccess", "use std.process.requireSuccess\nuse std.process.run\nvar result = run(\"printf\", [\"sushi\"])\nrequireSuccess(result)\nprintln(result.stdout)", output: "sushi\n"),

        Case("std.os.cwd", "use std.os.cwd\nprintln(cwd())", ignoreOutput: true, verify: (root, output, _) => Assert.Equal(Path.GetFullPath(root), output.TrimEnd('\r', '\n'))),
        Case("std.os.chdir", "use std.os.chdir\nuse std.os.cwd\nchdir(\"changed\")\nprintln(cwd())", ignoreOutput: true, setup: root => Directory.CreateDirectory(Path.Combine(root, "changed")), verify: (root, output, _) => Assert.Equal(Path.Combine(root, "changed"), output.TrimEnd('\r', '\n'))),

        Case("std.console.error", "use std.console.error\nerror(\"sushi error\")", error: "sushi error\n"),
        Case("std.console.readLine", "use std.console.readLine\nprintln(readLine())", output: "sushi input\n", input: "sushi input\n"),

        Case("std.archive.zip", "use std.archive.zip\nzip(\"archive-source\", \"archive.zip\")", setup: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "archive-source"));
            File.WriteAllText(Path.Combine(root, "archive-source", "file.txt"), "sushi");
        }, verify: (root, _, _) => Assert.True(File.Exists(Path.Combine(root, "archive.zip")))),
        Case("std.archive.unzip", "use std.archive.unzip\nunzip(\"source.zip\", \"extracted\")", setup: root =>
        {
            var source = Path.Combine(root, "zip-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "file.txt"), "sushi");
            System.IO.Compression.ZipFile.CreateFromDirectory(source, Path.Combine(root, "source.zip"));
        }, verify: (root, _, _) => Assert.Equal("sushi", File.ReadAllText(Path.Combine(root, "extracted", "file.txt")))),

        Case("std.http.get", "use std.http.get\nvar response = get(\"{{url}}\")\nprintln(response.status)\nprintln(response.body)", output: "200\nhello\n", needsHttpServer: true),
        Case("std.http.download", "use std.http.download\ndownload(\"{{url}}\", \"downloaded.txt\")", needsHttpServer: true, verify: (root, _, _) => Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "downloaded.txt")))),
        Case("std.http.post", "use std.http.post\nvar response = post(\"{{url}}\", \"sushi\")\nprintln(response.status)\nprintln(response.body)", output: "200\nposted:sushi\n", needsHttpServer: true)
    ];

    private static EndToEndCase Case(
        string name,
        string source,
        string output = "",
        string error = "",
        string input = "",
        string[]? arguments = null,
        int exitCode = 0,
        bool needsHttpServer = false,
        bool ignoreOutput = false,
        Action<string>? setup = null,
        Action<string, string, string>? verify = null) =>
        new(name, source, output, error, input, arguments ?? [], exitCode, needsHttpServer, ignoreOutput, setup, verify);

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    public sealed record EndToEndCase(
        string Name,
        string Source,
        string Output,
        string Error,
        string Input,
        IReadOnlyList<string> Arguments,
        int ExitCode,
        bool NeedsHttpServer,
        bool IgnoreOutput,
        Action<string>? Setup,
        Action<string, string, string>? Verify)
    {
        public override string ToString() => Name;
    }

    private sealed class HttpTestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _request;

        public HttpTestServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/resource";
            _request = Task.Run(ServeOnce);
        }

        public string Url { get; }

        private async Task ServeOnce()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            var contentLength = 0;
            string? header;
            while (!String.IsNullOrEmpty(header = await reader.ReadLineAsync()))
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    Int32.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);
            }

            var body = contentLength == 0 ? "" : new string(await ReadExactly(reader, contentLength));
            var responseBody = requestLine.StartsWith("POST ", StringComparison.Ordinal) ? "posted:" + body : "hello";
            var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\nConnection: close\r\n\r\n{responseBody}");
            await stream.WriteAsync(bytes);
        }

        private static async Task<char[]> ReadExactly(TextReader reader, int count)
        {
            var buffer = new char[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(offset, count - offset));
                if (read == 0) break;
                offset += read;
            }
            return buffer;
        }

        public void Dispose()
        {
            _listener.Stop();
            try { _request.Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }
        }
    }
}
