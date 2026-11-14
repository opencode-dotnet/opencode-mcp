using System.Globalization;
using System.Text.Json;
using System.Xml;
using NSubstitute;
using OpenCode.Mcp.Tools.Native;
using OpenCode.Mcp.Tools.Tests.Native.Support;
using OpenCode.Mcp.Tools.Tests.Support;

namespace OpenCode.Mcp.Tools.Tests.Native;

public sealed partial class NativeApplicationTests
{
    [Test]
    public async Task RunAsync_Should_Remove_The_Owned_Feed_When_Reporting_Its_Result_Fails(CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        using var output = Substitute.For<TextWriter>();
        output.WriteLineAsync(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(new IOException("fixture output failure")));
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(builder.Files, builder.Native.Runner, new FixtureHostReader(request.Host), builder.Native.Modes, output, errors);
        await Assert.That(await application.RunAsync(["pack-root", "--receipts", request.Output, "--feed", builder.Feed], token)).IsEqualTo(2);
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo("fixture output failure");
        await Assert.That(builder.Files.Directory.Exists(builder.Feed)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Report_Root_Packing_In_Manifest_Order_Or_The_Full_Refusal(bool refused, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateRootAsync(token);
        var request = await builder.PrepareCompleteAsync(token);
        if (refused)
        {
            builder.PackageCount = 0;
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(builder.Files, builder.Native.Runner, new FixtureHostReader(request.Host), builder.Native.Modes, output, errors);
        var result = await application.RunAsync(["pack-root", "--receipts", builder.Files.Path.GetRelativePath(request.Root, request.Output), "--feed", builder.Files.Path.GetRelativePath(request.Root, builder.Feed)], token);
        if (refused)
        {
            await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo(PackagingData.MissingFeedFile + builder.PackageFileName);
            await Assert.That(output.ToString()).IsEmpty();
            await Assert.That(result).IsEqualTo(2);
        }
        else
        {
            var expected = new List<string> { builder.Feed };
            string[] names = [builder.PackageFileName, .. request.Manifest.Runtimes.Select(runtime => builder.Identity.Id + "." + runtime.Rid + "." + PackagingData.BuildVersion + ".nupkg")];
            foreach (var name in names)
            {
                var bytes = await builder.Files.File.ReadAllBytesAsync(builder.Files.Path.Combine(builder.Feed, name), token);
                expected.Add(name + " SHA-256: " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            }
            await Assert.That(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n')).IsEqualTo(string.Join('\n', expected));
            await Assert.That(errors.ToString()).IsEmpty();
            await Assert.That(result).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Report_Child_Packing_Or_Its_Complete_Refusal(bool refused, CancellationToken token)
    {
        var builder = await PackageArchiveBuilder.CreateAsync(token);
        var request = await builder.PrepareStagedAsync(token);
        await new NativeEvidenceStore(builder.Files).WriteAsync(builder.Files.Path.Combine(request.Root, "packaging", "native-manifest.json"), request.Manifest, token);
        if (refused)
        {
            builder.PackageCount = 0;
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(builder.Files, builder.Native.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), builder.Native.Modes, output, errors);
        var exit = await application.RunAsync(["pack-child", "--rid", "win-x64", "--evidence", request.Output], token);
        if (refused)
        {
            await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo(PackagingData.PackageCountDiagnostic);
            await Assert.That(output.ToString()).IsEmpty();
            await Assert.That(exit).IsEqualTo(2);
        }
        else
        {
            var path = builder.Files.Path.Combine(request.Output, "packages", PackagingData.ChildFileName);
            await Assert.That(builder.Files.File.Exists(path)).IsTrue();
            var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await builder.Files.File.ReadAllBytesAsync(path, token)));
            await Assert.That(output.ToString().TrimEnd('\r', '\n')).IsEqualTo(path + "\nPackage SHA-256: " + digest);
            await Assert.That(errors.ToString()).IsEmpty();
            await Assert.That(exit).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Refuse_A_Forbidden_File_In_The_Final_Payload(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(request.Root, "packaging"));
        scenario.Files.File.Copy(manifest, scenario.Files.Path.Combine(request.Root, "packaging", "native-manifest.json"));
        var receipt = await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        await scenario.AddStagingInputsAsync(token);
        await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(receipt.PublishDirectory, "unexpected.deps.json"), "forbidden runtime metadata", token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var result = await application.RunAsync(["stage", "--rid", "win-x64", "--publish-directory", receipt.PublishDirectory], token);
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo("Forbidden runtime file: unexpected.deps.json");
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Write_Manifest_Matrices_Without_A_Rid(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        var path = scenario.Files.Path.Combine(request.Root, "github-output");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var result = await application.RunAsync(["matrix", "--manifest", manifest, "--github-output", path], token);
        await Assert.That(errors.ToString()).IsEmpty();
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(scenario.Files.File.Exists(path)).IsTrue();
        await Assert.That((await scenario.Files.File.ReadAllLinesAsync(path, token)).Select(static line => line.Split('=', 2)[0]))
            .IsEquivalentTo(["linux-matrix", "native-matrix", "consumer-matrix"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Name_The_Broken_Trx_Path_And_Rid(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        var report = scenario.Files.Path.Combine(request.Root, "artifacts", "test-results", "native", "win-x64", "published", "fixture.trx");
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(request.Root, "packaging"));
        scenario.Files.File.Copy(manifest, scenario.Files.Path.Combine(request.Root, "packaging", "native-manifest.json"));
        var respond = scenario.Runner.Response;
        scenario.Runner.Response = async (call, cancellation) =>
        {
            var result = await respond(call, cancellation);
            if (call.Arguments[0] == "test")
            {
                await scenario.Files.File.WriteAllTextAsync(report, "not XML", cancellation);
            }
            return result;
        };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var exit = await application.RunAsync(["verify", "--rid", "win-x64", "--output", scenario.Output], token);
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo("Malformed TRX input: win-x64: " + report);
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Name_The_Broken_Archive_Path_And_Rid(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        var published = await new NativeVerifier(scenario.Files, scenario.Runner).VerifyAsync(request, token);
        await scenario.AddStagingInputsAsync(token);
        var receipt = await new NativeStager(scenario.Files, scenario.Runner, scenario.Modes).StageAsync(request, published.PublishDirectory, token);
        var archive = scenario.Files.Path.Combine(scenario.Output, receipt.Archive ?? throw new InvalidOperationException());
        await scenario.Files.File.WriteAllTextAsync(archive, "not a ZIP archive", token);
        var store = new NativeEvidenceStore(scenario.Files);
        await store.WriteAsync(scenario.Files.Path.Combine(scenario.Output, "receipt.json"), receipt with { ArchiveSha256 = await store.DigestAsync(archive, token) }, token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var exit = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifest], token);
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo("Invalid archive input: win-x64: " + archive);
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("timeout", "Native command timed out.")]
    [Arguments("cancel", "Native command cancelled (Ctrl+C).")]
    [Arguments("json", "Malformed JSON input: fixture")]
    [Arguments("format", "Malformed toolchain or assets input: fixture")]
    [Arguments("key", "Missing toolchain or assets field: fixture")]
    [Arguments("aggregate", "Native command cleanup failed:\nIOException: first\nInvalidOperationException: second")]
    [Arguments("xml", "Malformed TRX input: fixture.trx")]
    [Arguments("archive", "Invalid archive input: fixture.zip")]
    public async Task RunAsync_Should_Name_Expected_Input_And_Cancellation_Failures(string failure, string diagnostic, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        scenario.Files.Directory.CreateDirectory(scenario.Files.Path.Combine(request.Root, "packaging"));
        scenario.Files.File.Copy(manifest, scenario.Files.Path.Combine(request.Root, "packaging", "native-manifest.json"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        scenario.Runner.Response = async (_, _) =>
        {
            if (failure == "cancel")
            {
                await cancellation.CancelAsync();
            }
            throw failure switch
            {
                "json" => new JsonException("fixture"),
                "format" => new FormatException("fixture"),
                "key" => new KeyNotFoundException("fixture"),
                "aggregate" => new AggregateException(new IOException("first"), new InvalidOperationException("second")),
                "xml" => new XmlException("fixture.trx"),
                "archive" => new InvalidDataException("fixture.zip"),
                _ => new OperationCanceledException("fixture"),
            };
        };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        int? result = null;
        await Assert.That(async () => result = await application.RunAsync(["verify", "--rid", "win-x64", "--output", scenario.Output], cancellation.Token)).ThrowsNothing();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(errors.ToString().TrimEnd('\r', '\n')).IsEqualTo(diagnostic);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments("rid")]
    [Arguments("payload")]
    [Arguments("path")]
    [Arguments("properties")]
    [Arguments("payload element")]
    public async Task RunAsync_Should_Name_A_Null_Required_Receipt_Field(string field, CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        scenario.Files.Directory.CreateDirectory(scenario.Output);
        var receipt = JsonSerializer.SerializeToNode(ReceiptFixture.Create(request.Manifest.Runtimes[0]), JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException();
        if (field == "path")
        {
            var entry = receipt["payload"]?[0] ?? throw new InvalidOperationException();
            entry["path"] = null;
        }
        else if (field == "properties")
        {
            var properties = receipt["properties"] ?? throw new InvalidOperationException();
            properties["PublishAot"] = null;
        }
        else if (field == "payload element")
        {
            var payload = receipt["payload"] ?? throw new InvalidOperationException();
            payload[0] = null;
        }
        else
        {
            receipt[field] = null;
        }
        await scenario.Files.File.WriteAllTextAsync(scenario.Files.Path.Combine(scenario.Output, "receipt.json"), receipt.ToJsonString(), token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        int? result = null;
        await Assert.That(async () => result = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifest], token)).ThrowsNothing();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(errors.ToString()).StartsWith("Malformed JSON input: ");
        await Assert.That(errors.ToString()).Contains(field == "payload element" ? "payload" : field);
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Use_The_Explicit_Manifest_And_Report_The_Full_Incomplete_Set(CancellationToken token)
    {
        var scenario = new NativeBuildScenario();
        var request = await scenario.PrepareAsync(token);
        var manifest = await new NativeManifestFixture().WriteAsync(scenario.Files, token);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var application = new NativeApplication(scenario.Files, scenario.Runner, new FixtureHostReader(request.Host with { WorkingDirectory = request.Root }), scenario.Modes, output, errors);
        var result = await application.RunAsync(["validate", "--receipts", scenario.Output, "--manifest", manifest], token);
        await Assert.That(errors.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(
            "Missing receipt: win-x64\nMissing receipt: win-arm64\nMissing receipt: linux-x64\nMissing receipt: linux-arm64\nMissing receipt: osx-x64\nMissing receipt: osx-arm64\n");
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(result).IsEqualTo(2);
        await Assert.That(scenario.Runner.Calls).IsEmpty();
    }
}
