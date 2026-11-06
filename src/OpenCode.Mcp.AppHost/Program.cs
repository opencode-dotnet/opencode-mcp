using OpenCode.Mcp.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
AppHostComposition.Configure(builder);
var app = builder.Build();
await using (app.ConfigureAwait(false))
{
    // The application lifetime owns cancellation for the development entry point.
    await app.RunAsync(CancellationToken.None).ConfigureAwait(false);
}
