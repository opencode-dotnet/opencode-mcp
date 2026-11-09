namespace OpenCode.Mcp.Tools.Native;

internal static class NativeReceiptPolicy
{
    public static IReadOnlyList<string> Diagnostics(NativeManifest manifest, IReadOnlyList<NativeReceipt> receipts)
    {
        var diagnostics = new List<string>();
        foreach (var rid in manifest.Runtimes.Select(static runtime => runtime.Rid))
        {
            var matching = receipts.Where(receipt => string.Equals(receipt.Rid, rid, StringComparison.Ordinal)).ToArray();
            if (matching.Length == 0)
            {
                diagnostics.Add("Missing receipt: " + rid);
            }
            else if (matching.Length > 1)
            {
                diagnostics.Add("Duplicate receipt: " + rid);
            }
            else if (new NativeRid(rid).IsLinux && matching[0].RuntimeIndependence is null)
            {
                diagnostics.Add("Missing Linux runtime-independence proof: " + rid);
            }
            if (matching.Length == 1)
            {
                diagnostics.AddRange(EntryDiagnostics(matching[0], manifest));
                diagnostics.AddRange(ProofDiagnostics(matching[0], manifest));
            }
        }
        diagnostics.AddRange(receipts.Select(static receipt => receipt.Rid).Except(manifest.Runtimes.Select(static runtime => runtime.Rid), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(static rid => "Unexpected receipt: " + rid));
        CheckCommon(receipts.Select(static receipt => receipt.SourceCommit), "SourceCommit", diagnostics);
        CheckCommon(receipts.Select(static receipt => receipt.SdkVersion), "SdkVersion", diagnostics);
        CheckCommon(receipts.Select(static receipt => receipt.CommonInputsSha256), "CommonInputsSha256", diagnostics);
        CheckCommon(receipts.Select(static receipt => receipt.Version), "Version", diagnostics);
        CheckCommon(receipts.Select(static receipt => receipt.DependenciesSha256), "DependenciesSha256", diagnostics);
        return diagnostics.AsReadOnly();
    }

    public static string[] EntryDiagnostics(NativeReceipt receipt, NativeManifest manifest)
    {
        var diagnostics = NativePublishPolicy.PropertyDiagnostics(receipt.Properties).Select(error => receipt.Rid + ": " + error).ToList();
        var rid = new NativeRid(receipt.Rid);
        if (receipt.RequestedRunner != manifest.Runtimes.Single(runtime => runtime.Rid == receipt.Rid).Runner)
        {
            diagnostics.Add(receipt.Rid + ": RequestedRunner differs from manifest.");
        }
        if (receipt.BuilderImage != (rid.IsLinux ? manifest.BuilderImage : null))
        {
            diagnostics.Add(receipt.Rid + ": BuilderImage differs from manifest.");
        }
        if (receipt.RuntimeImage != (rid.IsLinux ? manifest.RuntimeImage : null))
        {
            diagnostics.Add(receipt.Rid + ": RuntimeImage differs from manifest.");
        }
        if (receipt.PublishExitCode != 0)
        {
            diagnostics.Add("Unsuccessful publish: " + receipt.Rid);
        }
        if (receipt.Warnings.Count != 0)
        {
            diagnostics.Add("Publish warnings: " + receipt.Rid);
        }
        if (!receipt.PublishedTests.Succeeded)
        {
            diagnostics.Add("Unsuccessful published tests: " + receipt.Rid);
        }
        if (receipt.NormalizedTests?.Succeeded != true)
        {
            diagnostics.Add("Unsuccessful normalized tests: " + receipt.Rid);
        }
        if (receipt.ExtractedTests?.Succeeded != true)
        {
            diagnostics.Add("Unsuccessful extracted tests: " + receipt.Rid);
        }
        if (receipt.Payload.Count == 0)
        {
            diagnostics.Add("Missing payload inventory: " + receipt.Rid);
        }
        else
        {
            foreach (var name in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md", rid.CommandName, "payload.json" }
                .Where(name => !receipt.Payload.Any(file => file.Path == name)))
            {
                diagnostics.Add(receipt.Rid + ": Missing required payload file: " + name);
            }
            var command = receipt.Payload.FirstOrDefault(file => file.Path == rid.CommandName);
            if (command is not null && command.Sha256 != receipt.ExecutableSha256)
            {
                diagnostics.Add(receipt.Rid + ": Payload command digest differs from verified executable.");
            }
        }
        if (string.IsNullOrEmpty(receipt.ArchiveSha256))
        {
            diagnostics.Add("Missing archive identity: " + receipt.Rid);
        }
        diagnostics.AddRange(NativePublishPolicy.HostDiagnostics(receipt.Rid, receipt.Os, receipt.OsArchitecture, receipt.ProcessArchitecture).Select(error => receipt.Rid + ": " + error));
        return [.. diagnostics];
    }

    private static IEnumerable<string> ProofDiagnostics(NativeReceipt receipt, NativeManifest manifest)
    {
        var rid = new NativeRid(receipt.Rid);
        var proof = receipt.RuntimeIndependence;
        if (proof is null)
        {
            if (!rid.IsLinux)
            {
                yield return "Missing matching-host runtime-independence proof: " + receipt.Rid;
            }
        }
        else
        {
            var kind = rid.IsLinux ? "linux-clean-container" : "matching-host-inspection";
            if (proof.Kind != kind)
            {
                yield return receipt.Rid + ": RuntimeIndependence.Kind must be " + kind + ".";
            }
            if (rid.IsLinux)
            {
                if (proof.Tests?.Succeeded != true)
                {
                    yield return receipt.Rid + ": RuntimeIndependence.Tests must be successful and nonzero.";
                }
                if (proof.ImageReference != manifest.RuntimeImage)
                {
                    yield return receipt.Rid + ": RuntimeIndependence.ImageReference differs from manifest.";
                }
                if (string.IsNullOrWhiteSpace(proof.ImageId))
                {
                    yield return receipt.Rid + ": RuntimeIndependence.ImageId is required.";
                }
                if (proof.DotnetAbsent != true)
                {
                    yield return receipt.Rid + ": RuntimeIndependence.DotnetAbsent must be true.";
                }
            }
            else if (NativeInspectionPolicy.Diagnostics([], receipt.Rid, proof.Inspection ?? "").Count != 0)
            {
                yield return receipt.Rid + ": RuntimeIndependence.Inspection must contain the native AOT export.";
            }
        }
        if (rid.IsLinux && receipt.GlibcFloor is null)
        {
            yield return receipt.Rid + ": GlibcFloor is required.";
        }
        if (rid.IsLinux && receipt.GlibcFloor is { } floor && (!Version.TryParse(floor.Version, out _)
            || !floor.Version.All(static character => char.IsAsciiDigit(character) || character == '.') || string.IsNullOrWhiteSpace(floor.ToolVersion)))
        {
            yield return receipt.Rid + ": GlibcFloor requires a numeric Version and ToolVersion.";
        }
        if (!rid.IsLinux && receipt.GlibcFloor is not null)
        {
            yield return receipt.Rid + ": GlibcFloor must be null.";
        }
    }

    private static void CheckCommon(IEnumerable<string> values, string name, List<string> diagnostics)
    {
        if (values.Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            diagnostics.Add("Mixed receipt field: " + name);
        }
    }
}
