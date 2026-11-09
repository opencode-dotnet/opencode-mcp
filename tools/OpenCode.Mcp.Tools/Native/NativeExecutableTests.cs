using System.Globalization;
using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeExecutableTests(IFileSystem files, NativeProcesses processes, NativeRid rid)
{
    public Task<NativeTestRun> RunAsync(string executable, string results, CancellationToken token) => RunAsync(executable, results, null, token);

    public async Task<NativeTestRun> RunAsync(string executable, string results, string? containerImage, CancellationToken token)
    {
        if (files.Directory.Exists(results))
        {
            files.Directory.Delete(results, recursive: true);
        }
        files.Directory.CreateDirectory(results);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OPENCODE_MCP_TEST_EXECUTABLE"] = executable,
            ["OPENCODE_MCP_TEST_EXPECT_NATIVE"] = "true",
            ["OPENCODE_MCP_TEST_CONTAINER_IMAGE"] = containerImage,
            ["TUNIT_DISABLE_ARTIFACT_UPLOAD"] = "true",
        };
        var result = await processes.RunAsync("dotnet", ["test", "--project", "tests/OpenCode.Mcp.Tests/OpenCode.Mcp.Tests.csproj", "-c", "Release", "--no-build",
            "--treenode-filter", "/*/*/ExecutableTests/*", "--minimum-expected-tests", "1", "--report-trx", "--results-directory", results,
            "--timeout", "25m", "--hangdump", "--hangdump-timeout", "15m", "--hangdump-type", "Mini"], environment, token).ConfigureAwait(false);
        await files.File.WriteAllTextAsync(files.Path.Combine(results, "execution.log"), result.Output, token).ConfigureAwait(false);
        var reports = files.Directory.EnumerateFiles(results, "*.trx", SearchOption.AllDirectories).ToArray();
        var counters = new List<XElement>();
        foreach (var report in reports)
        {
            try
            {
                var document = XDocument.Parse(await files.File.ReadAllTextAsync(report, token).ConfigureAwait(false));
                counters.Add(document.Descendants().Single(static element => element.Name.LocalName == "Counters"));
            }
            catch (XmlException error)
            {
                throw new InvalidOperationException("Malformed TRX input: " + rid.Value + ": " + report, error);
            }
        }
        var run = new NativeTestRun(result.ExitCode, Sum(counters, "passed"), Sum(counters, "failed"), Sum(counters, "notExecuted"));
        if (!run.Succeeded)
        {
            throw new InvalidOperationException($"Native executable tests failed: {run}\n{result.Output}");
        }
        return run;
    }

    private static int Sum(IEnumerable<XElement> counters, string attribute) => counters.Sum(counter =>
        int.Parse(counter.Attribute(attribute)?.Value ?? throw new InvalidOperationException("Missing TRX counter: " + attribute), CultureInfo.InvariantCulture));
}
