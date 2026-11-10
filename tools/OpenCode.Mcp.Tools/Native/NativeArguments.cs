namespace OpenCode.Mcp.Tools.Native;

internal static class NativeArguments
{
    public static NativeCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("Arguments: use verify, stage, inspect, validate, matrix, control, prove-linux, pack-child, pack-root or snapshot.");
        }
        var operation = arguments[0];
        string[] required = operation switch
        {
            "verify" => ["--rid"],
            "stage" => ["--rid", "--publish-directory"],
            "inspect" => ["--rid", "--path"],
            "validate" => ["--receipts"],
            "matrix" => ["--manifest", "--github-output"],
            "control" => ["--rid"],
            "prove-linux" => ["--rid", "--evidence"],
            "pack-child" => ["--rid", "--evidence"],
            "pack-root" => ["--receipts", "--feed"],
            "snapshot" => ["--output"],
            _ => throw new InvalidOperationException("Arguments: unknown command " + operation + "."),
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            var optional = (operation == "verify" && name == "--output") || (operation == "validate" && name == "--manifest");
            if (!required.Contains(name, StringComparer.Ordinal) && !optional)
            {
                throw new InvalidOperationException($"Arguments: unknown option {name} for {operation}.");
            }
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Arguments: missing value for " + name + ".");
            }
            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new InvalidOperationException("Arguments: duplicate " + name + ".");
            }
        }
        var missing = required.FirstOrDefault(name => !values.ContainsKey(name));
        if (missing is not null)
        {
            throw new InvalidOperationException("Arguments: missing " + missing + ".");
        }
        var command = new NativeCommand(operation, values.GetValueOrDefault("--rid"), values.GetValueOrDefault("--output"),
            values.GetValueOrDefault("--publish-directory"), values.GetValueOrDefault("--path"), values.GetValueOrDefault("--receipts"),
            GithubOutput: values.GetValueOrDefault("--github-output"), Evidence: values.GetValueOrDefault("--evidence"), Feed: values.GetValueOrDefault("--feed"));
        return values.TryGetValue("--manifest", out var manifest) ? command with { Manifest = manifest } : command;
    }
}
