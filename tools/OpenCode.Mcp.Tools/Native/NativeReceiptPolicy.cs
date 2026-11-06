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
            else if (rid.StartsWith("linux-", StringComparison.Ordinal) && string.IsNullOrEmpty(matching[0].RuntimeIndependence))
            {
                diagnostics.Add("Missing Linux runtime-independence proof: " + rid);
            }
            if (matching.Length == 1)
            {
                diagnostics.AddRange(EntryDiagnostics(matching[0]));
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

    private static string[] EntryDiagnostics(NativeReceipt receipt)
    {
        var diagnostics = NativePublishPolicy.PropertyDiagnostics(receipt.Properties).Select(error => receipt.Rid + ": " + error).ToList();
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
        if (string.IsNullOrEmpty(receipt.ArchiveSha256))
        {
            diagnostics.Add("Missing archive identity: " + receipt.Rid);
        }
        return [.. diagnostics];
    }

    private static void CheckCommon(IEnumerable<string> values, string name, List<string> diagnostics)
    {
        if (values.Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            diagnostics.Add("Mixed receipt field: " + name);
        }
    }
}
