$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot '../src/OpenCode.Mcp.AppHost/OpenCode.Mcp.AppHost.csproj'
[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$sdkParts = $project.Project.Sdk.Split('/')
if ($sdkParts.Count -ne 2 -or $sdkParts[0] -ne 'Aspire.AppHost.Sdk') {
    throw 'AppHost SDK must have an explicit version.'
}
& dotnet dnx "Aspire.Cli@$($sdkParts[1])" --yes -- @args
exit $LASTEXITCODE
