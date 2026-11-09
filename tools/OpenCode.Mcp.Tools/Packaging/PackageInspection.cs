using System.Text.Json;
using OpenCode.Mcp.Tools.Native;

namespace OpenCode.Mcp.Tools.Packaging;

internal sealed record PackageInspection
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> PackageTypes { get; init; }
    public required string SettingsPath { get; init; }
    public required string CommandName { get; init; }
    public required string Runner { get; init; }
    public required string EntryPoint { get; init; }
    public required string ManifestPath { get; init; }
    public required JsonElement Manifest { get; init; }
    public required long ManifestSize { get; init; }
    public required IReadOnlyList<PayloadFile> Payload { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public string? RepositoryCommit { get; init; }
}
