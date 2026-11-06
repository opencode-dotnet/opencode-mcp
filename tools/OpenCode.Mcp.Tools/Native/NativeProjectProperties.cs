using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativeProjectProperties(IFileSystem files, NativeProcesses processes)
{
    public const string ProductProject = "src/OpenCode.Mcp/OpenCode.Mcp.csproj";

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(NativeBuildRequest request, string sdk,
        IReadOnlyDictionary<string, string?> environment, CancellationToken token)
    {
        var output = await processes.RequireAsync("dotnet", ["msbuild", ProductProject, "-p:Configuration=Release", "-p:RuntimeIdentifier=" + request.Rid,
            "-getProperty:PublishAot,InvariantGlobalization,IlcTreatWarningsAsErrors,DebuggerSupport,Version,NETCoreSdkVersion"], environment, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var properties = document.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value.GetString() ?? "", StringComparer.Ordinal);
        await new NativeEvidenceStore(files).WriteAsync(files.Path.Combine(request.Output, "properties.json"), properties, token).ConfigureAwait(false);
        NativeDiagnostics.Refuse(NativePublishPolicy.PropertyDiagnostics(properties));
        if (!properties.TryGetValue("NETCoreSdkVersion", out var evaluatedSdk) || !string.Equals(sdk, evaluatedSdk, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Evaluated SDK must match executed SDK: expected {sdk}; observed {evaluatedSdk}.");
        }
        return properties;
    }
}
