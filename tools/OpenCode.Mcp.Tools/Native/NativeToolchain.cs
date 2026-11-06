namespace OpenCode.Mcp.Tools.Native;

internal sealed record NativeToolchain(string Inspector, IReadOnlyList<string> InspectorArguments,
    IReadOnlyDictionary<string, string?> PublishEnvironment, string Details);
