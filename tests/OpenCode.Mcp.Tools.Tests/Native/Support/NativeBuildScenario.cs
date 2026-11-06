using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Native.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class NativeBuildScenario
{
    public MockFileSystem Files { get; } = new();
    public FakeNativeProcessRunner Runner { get; } = new();
    public FakeUnixFileModes Modes { get; } = new();
    public string Root => Files.Path.Combine(Files.Path.GetTempPath(), "repository");
    public string Installer => Files.Path.Combine(Files.Path.GetTempPath(), "ProgramFiles", "Microsoft Visual Studio", "Installer");
    public string VisualStudio => Files.Path.Combine(Files.Path.GetTempPath(), "VisualStudio");
    public string Output => Files.Path.Combine(Root, "artifacts", "native", "win-x64");
    public string Dumpbin => Files.Path.Combine(VisualStudio, "VC", "Tools", "MSVC", "14.50", "bin", "Hostx64", "x64", "dumpbin.exe");
    public string WindowsSdk => Files.Path.Combine(Files.Path.GetTempPath(), "ProgramFiles", "Windows Kits", "10", "Lib");
    public string PublishOutput { get; set; } = "Publish succeeded.";
    public int PublishExitCode { get; set; }
    public string Exports { get; set; } = "1 00000000 DotNetRuntimeDebugHeader";
    public bool ComponentFound { get; set; } = true;
    public string SdkArchitecture { get; set; } = "x64";
    public JsonObject EffectiveProperties { get; } = new()
    {
        ["PublishAot"] = "true",
        ["InvariantGlobalization"] = "true",
        ["IlcTreatWarningsAsErrors"] = "true",
        ["DebuggerSupport"] = "",
        ["Version"] = "fixture-version",
        ["NETCoreSdkVersion"] = "10.0.403",
        ["NoWarn"] = "",
        ["SuppressTrimAnalysisWarnings"] = "",
        ["SuppressAotAnalysisWarnings"] = "",
    };

    public async Task AddStagingInputsAsync(CancellationToken token)
    {
        await WriteAsync(Files.Path.Combine(Output, "publish", "required.dat"), "required asset", token);
        await WriteAsync(Files.Path.Combine(Output, "publish", "debug.pdb"), "symbol", token);
        await WriteAsync(Files.Path.Combine(Root, "LICENSE"), "fixture license", token);
        await WriteAsync(Files.Path.Combine(Root, "THIRD-PARTY-NOTICES.md"),
            "<!-- native-package: ModelContextProtocol/2.3.1 -->\nMCP fixture notice\n<!-- native-package: Polly.Core/8.4.2 -->\nPolly fixture notice\n", token);
    }

    public async Task<string> PrepareUnixPayloadAsync(NativeBuildRequest request, bool changedMode, CancellationToken token)
    {
        var runtime = request.Manifest.Runtimes.Single(static runtime => runtime.Rid == "linux-x64");
        var manifest = request.Manifest with { Runtimes = [runtime] };
        var store = new NativeEvidenceStore(Files);
        var manifestPath = Files.Path.Combine(request.Root, "manifest.json");
        await store.WriteAsync(manifestPath, manifest, token);
        var payload = Files.Path.Combine(Output, "payload");
        Files.Directory.CreateDirectory(payload);
        var receipt = ReceiptFixture.Create(runtime);
        var inventory = new List<PayloadFile>();
        foreach (var file in receipt.Payload)
        {
            var bytes = Encoding.UTF8.GetBytes(file.Path);
            await Files.File.WriteAllBytesAsync(Files.Path.Combine(payload, file.Path), bytes, token);
            inventory.Add(file with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.LongLength, UnixMode = 493 });
        }
        receipt = receipt with { Payload = inventory, Archive = "payload.tar.gz", ExecutableSha256 = inventory.Single(static file => file.Path == "opencode-mcp").Sha256 };
        await store.WriteAsync(Files.Path.Combine(Output, "payload-manifest.json"), inventory, token);
        var archivePath = Files.Path.Combine(Output, receipt.Archive);
        var archiveInventory = inventory.Select(file => changedMode && file.Path == "opencode-mcp" ? file with { UnixMode = 420 } : file).ToArray();
        await new PayloadArchive(Files, Modes, new NativeRid(runtime.Rid)).CreateAsync(payload, archivePath, archiveInventory, token);
        receipt = receipt with { ArchiveSha256 = await store.DigestAsync(archivePath, token) };
        await store.WriteAsync(Files.Path.Combine(Output, "receipt.json"), receipt, token);
        return manifestPath;
    }

    public async Task<NativeBuildRequest> PrepareAsync(CancellationToken token)
    {
        Files.Directory.CreateDirectory(Root);
        await WriteAsync(Files.Path.Combine(Installer, "vswhere.exe"), "fixture", token);
        await WriteAsync(Dumpbin, "fixture", token);
        await WriteAsync(Files.Path.Combine(Files.Path.GetDirectoryName(Dumpbin) ?? throw new InvalidOperationException(), "link.exe"), "fixture", token);
        await WriteAsync(Files.Path.Combine(WindowsSdk, "10.0.26100.0", "um", "x64", "kernel32.lib"), "fixture", token);
        await WriteAsync(Files.Path.Combine(Root, "Directory.Build.props"), "fixture-input", token);
        var distribution = new DistributionNoticeFixture(Files);
        await distribution.PrepareAsync(token);
        var assets = distribution.Assets;
        await WriteAsync(Files.Path.Combine(Root, "src", "OpenCode.Mcp", "obj", "project.assets.json"), assets.ToJsonString(), token);
        Runner.Response = RespondAsync;
        var manifest = await new NativeManifestStore(Files).ReadAsync(await new NativeManifestFixture().WriteAsync(Files, token), token);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "original-path",
            ["ProgramFiles(x86)"] = Files.Path.Combine(Files.Path.GetTempPath(), "ProgramFiles"),
        };
        return new NativeBuildRequest(Root, Output, "win-x64", new NativeHost("win", "x64", "x64", "fixture-host", environment), manifest);
    }

    private async Task<NativeProcessResult> RespondAsync(NativeProcessRequest request, CancellationToken token)
    {
        var command = request.Arguments.Count == 0 ? "" : request.Arguments[0];
        var output = "";
        if (request.FileName.EndsWith("vswhere.exe", StringComparison.Ordinal))
        {
            output = ComponentFound ? VisualStudio : "";
        }
        else if (request.FileName == Dumpbin)
        {
            output = Exports;
        }
        else if (request.FileName.EndsWith("link.exe", StringComparison.Ordinal))
        {
            output = "Linker fixture version 14.50";
        }
        else if (request.FileName == "git")
        {
            output = command switch { "rev-parse" => "fixture-commit", "ls-files" => "Directory.Build.props\0", _ => "" };
        }
        else if (command == "--version")
        {
            output = "10.0.403";
        }
        else if (command == "--info")
        {
            output = "Architecture: " + SdkArchitecture;
        }
        else if (request.Arguments.Any(static argument => argument.StartsWith("-getProperty:", StringComparison.Ordinal)))
        {
            await Assert.That(request.Arguments).Contains("-p:RuntimeIdentifier=win-x64");
            output = new JsonObject
            {
                ["Properties"] = new JsonObject(request.Arguments.Single(static argument => argument.StartsWith("-getProperty:", StringComparison.Ordinal))
                    ["-getProperty:".Length..].Split(',').Where(EffectiveProperties.ContainsKey)
                    .Select(name => new KeyValuePair<string, JsonNode?>(name, EffectiveProperties[name]?.DeepClone()))),
            }.ToJsonString();
        }
        else if (command == "restore")
        {
            await Assert.That(request.Arguments).IsEquivalentTo(["restore", "src/OpenCode.Mcp/OpenCode.Mcp.csproj", "-r", "win-x64", "-nodeReuse:false", "-p:UseSharedCompilation=false"], CollectionOrdering.Matching);
        }
        else if (command == "publish")
        {
            await WriteAsync(Files.Path.Combine(Output, "publish", "OpenCode.Mcp.exe"), "fixture-native-image", token);
            return new NativeProcessResult(PublishExitCode, "", PublishOutput);
        }
        else if (command == "test")
        {
            var index = request.Arguments.ToList().IndexOf("--results-directory");
            var result = new XDocument(new XElement("TestRun", new XElement("ResultSummary",
                new XElement("Counters", new XAttribute("passed", 12), new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))));
            await WriteAsync(Files.Path.Combine(request.Arguments[index + 1], "fixture.trx"), result.ToString(), token);
        }
        return new NativeProcessResult(0, output, "");
    }

    private async Task WriteAsync(string path, string text, CancellationToken token)
    {
        Files.Directory.CreateDirectory(Files.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        await Files.File.WriteAllTextAsync(path, text, token);
    }
}
