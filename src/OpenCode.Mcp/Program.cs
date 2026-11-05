using OpenCode.Mcp;

// The generic host lifetime owns Ctrl+C and SIGTERM; the selector runs before any host exists.
return await McpApp.CreateApplication(Console.Error).RunAsync(args, CancellationToken.None).ConfigureAwait(false);
