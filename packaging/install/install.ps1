function Invoke-OpenCodeMcpInstaller {
[CmdletBinding()]
param(
    [string]$Version,
    [string]$InstallDir,
    [switch]$AddToPath,
    [switch]$Uninstall,
    [string]$AssetDir
)

$ErrorActionPreference = 'Stop'
$Options = $PSBoundParameters
$FileInvocation = [bool]{}.File

function Write-InstallerMessage($Message) { Write-Output ('opencode-mcp: ' + $Message) }
function Test-InstallerVersion($Value) {
    if ($Value -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z') { return $false }
    $core = ($Value -split '\+')[0]
    if ($core.Contains('-')) {
        foreach ($identifier in $core.Substring($core.IndexOf('-') + 1).Split('.')) {
            if ($identifier -cmatch '^0[0-9]+$') { return $false }
        }
    }
    return $true
}
function Get-InstallerPath($Value, $Option) {
    if ([string]::IsNullOrEmpty($Value) -or $Value.IndexOfAny([char[]]"`r`n") -ge 0) { $state.Refused = $true; throw "opencode-mcp: Option requires a value: $Option." }
    try {
        $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Value)
        if ($full -ceq [IO.Path]::GetPathRoot($full)) { $full } else { $full.TrimEnd('\', '/') }
    }
    catch { $state.Refused = $true; throw "opencode-mcp: Install collision: $Value." }
}
function Get-InstallerHash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-InstallerInventory($Directory) {
    $entries = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($Directory.Length + 1).Replace('\', '/')
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $state.Refused = $true; throw "opencode-mcp: Installed files differ from the ownership record: $relative." }
        if ($_.PSIsContainer) { 'directory  ' + $relative }
        else { (Get-InstallerHash $_.FullName) + '  ' + $relative }
    })
    [Array]::Sort($entries, [StringComparer]::Ordinal)
    $entries
}
function Get-InstallerRecord($Name) {
    $path = Join-Path $state.Private $Name
    if (-not [IO.File]::Exists($path) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
    [IO.File]::ReadAllLines($path)
}
function Test-InstallerPayload {
    $inventory = @(Get-InstallerRecord 'inventory')
    $payload = Join-Path $state.Install 'current'
    if (-not [IO.Directory]::Exists($payload) -or ((Get-Item -LiteralPath $payload -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $state.Refused = $true; throw 'opencode-mcp: Installed files differ from the ownership record: current.' }
    foreach ($entry in $inventory) {
        $parts = $entry -split '  ', 2
        if ($parts.Count -ne 2 -or [IO.Path]::IsPathRooted($parts[1]) -or $parts[1] -match '(^|[\\/])\.\.([\\/]|$)') { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
        $path = Join-Path $payload $parts[1]
        if (-not (Test-Path -LiteralPath $path) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $state.Refused = $true; throw "opencode-mcp: Installed files differ from the ownership record: $($parts[1])." }
        if ($parts[0] -ceq 'directory') {
            if (-not [IO.Directory]::Exists($path)) { $state.Refused = $true; throw "opencode-mcp: Installed files differ from the ownership record: $($parts[1])." }
        } elseif (-not [IO.File]::Exists($path) -or (Get-InstallerHash $path) -cne $parts[0]) {
            $state.Refused = $true
            throw "opencode-mcp: Installed files differ from the ownership record: $($parts[1])."
        }
    }
    $actual = @(Get-InstallerInventory $payload)
    if (($actual -join "`n") -cne ($inventory -join "`n")) {
        $extra = @($actual | Where-Object { $inventory -cnotcontains $_ })
        $relative = if ($extra.Count) { ($extra[0] -split '  ', 2)[1] } else { 'inventory' }
        $state.Refused = $true
        throw "opencode-mcp: Installed files differ from the ownership record: $relative."
    }
}
function Get-InstallerOwnership {
    if (-not (Test-Path -LiteralPath $state.Private)) {
        return
    }
    if (((Get-Item -LiteralPath $state.Private -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
    $owner = @(Get-InstallerRecord 'owner')
    if ($owner.Count -ne 3 -or $owner[0] -cne 'opencode-mcp' -or -not [string]::Equals($owner[1], $state.Install, [StringComparison]::OrdinalIgnoreCase) -or $owner[2] -notin @('0','1')) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
    $selection = @(Get-InstallerRecord 'current')
    if ($selection.Count -ne 2 -or -not (Test-InstallerVersion $selection[0]) -or $selection[1] -notin @('win-x64','win-arm64')) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
    $state.OldVersion = $selection[0]; $state.OldRid = $selection[1]
    $state.Created = $owner[2] -ceq '1'
    Test-InstallerPayload
    $state.Owned = $true
}
function Get-InstallerUserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    try {
        if ($null -eq $key) { return @{ Value = $null; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString } }
        $value = $key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $kind = if ($null -ne $value) { $key.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
        if ($kind -notin @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
        @{ Value = $value; Kind = $kind }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
function Invoke-InstallerPathBroadcast {
    if (-not ('OpenCodeMcpInstaller.NativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace OpenCodeMcpInstaller {
    public static class NativeMethods {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
            UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    }
}
'@
    }
    $result = [UIntPtr]::Zero
    [void][OpenCodeMcpInstaller.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x1a, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}
function Invoke-InstallerPathWrite($Value, $Kind) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try {
        if ($null -eq $Value) { $key.DeleteValue('Path', $false) }
        else { $key.SetValue('Path', $Value, $Kind) }
    } finally { $key.Dispose() }
    try { Invoke-InstallerPathBroadcast } catch { return } # Notification is best-effort after the registry write.
}
function Get-InstallerPathOwnership {
    $state.PathOwned = $false
    if (-not (Test-Path -LiteralPath (Join-Path $state.Private 'path.record'))) { return }
    $record = @(Get-InstallerRecord 'path.record')
    $token = Join-Path $state.Install 'current'
    if ($record.Count -ne 2 -or $record[0] -cne $token -or $record[1] -notin @('absent','present')) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
    $current = Get-InstallerUserPath
    $tokens = @(([string]$current.Value).Split(';'))
    if (@($tokens | Where-Object { $_ -ieq $token }).Count -ne 1 -or $tokens -cnotcontains $token) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
    $state.PathOwned = $true
    $state.PathOriginallyAbsent = $record[1] -ceq 'absent'
}
function Invoke-InstallerPathRemoval {
    if (-not $state.PathOwned) { return }
    Get-InstallerPathOwnership
    $current = Get-InstallerUserPath
    $token = Join-Path $state.Install 'current'
    $tokens = @(([string]$current.Value).Split(';'))
    $remaining = @($tokens | Where-Object { $_ -cne $token })
    $value = $remaining -join ';'
    if ($state.PathOriginallyAbsent -and $value -ceq '') { $value = $null }
    Invoke-InstallerPathWrite $value $current.Kind
}
function Invoke-InstallerPathAddition {
    if (-not $state.AddPath -or $state.PathOwned) { return }
    $token = Join-Path $state.Install 'current'
    $current = Get-InstallerUserPath
    if (@(([string]$current.Value).Split(';') | Where-Object { $_ -ieq $token }).Count -gt 0) { return }
    $state.PathOriginallyAbsent = $null -eq $current.Value
    $state.PathWritten = $true
    $value = if ([string]::IsNullOrEmpty($current.Value)) { $token } else { $current.Value + ';' + $token }
    Invoke-InstallerPathWrite $value $current.Kind
    $absent = if ($state.PathOriginallyAbsent) { 'absent' } else { 'present' }
    [IO.File]::WriteAllLines((Join-Path $state.Work 'path.record'), @($token, $absent), (New-Object Text.UTF8Encoding $false))
    $state.PathOwned = $true
    $state.PathChanged = $true
}
function Invoke-InstallerRecordReplace($Source, $Destination) {
    if ([IO.File]::Exists($Destination)) { [IO.File]::Replace($Source, $Destination, $null) }
    else { [IO.File]::Move($Source, $Destination) }
}
function Invoke-InstallerCleanup {
    if (-not $state.Work) { return }
    $errors = @()
    if (-not $state.Success) {
        if ($state.PathWritten) {
            try {
                # Reverse only our inserted token from the latest raw value.
                $current = Get-InstallerUserPath
                $token = Join-Path $state.Install 'current'
                $tokens = @(([string]$current.Value).Split(';'))
                if (@($tokens | Where-Object { $_ -ieq $token }).Count -ne 1 -or $tokens -cnotcontains $token) { throw 'PATH rollback ownership is ambiguous.' }
                $value = @($tokens | Where-Object { $_ -cne $token }) -join ';'
                if ($state.PathOriginallyAbsent -and $value -ceq '') { $value = $null }
                Invoke-InstallerPathWrite $value $current.Kind
            } catch { $errors += $_ }
        }
        if ($state.Activated) {
            try {
                $state.Staging = Join-Path $state.Private ('removing-' + [guid]::NewGuid().ToString('N'))
                [IO.Directory]::Move((Join-Path $state.Install 'current'), $state.Staging)
            } catch { $errors += $_ }
        }
        if ($state.Previous -and [IO.Directory]::Exists($state.Previous)) {
            try { [IO.Directory]::Move($state.Previous, (Join-Path $state.Install 'current')) } catch { $errors += $_ }
        }
        if ($state.RecordsStarted) {
            foreach ($name in @('owner','current','inventory','path.record')) {
                try {
                    $backup = Join-Path $state.Work ('old-' + $name)
                    $destination = Join-Path $state.Private $name
                    if ([IO.File]::Exists($backup)) { Invoke-InstallerRecordReplace $backup $destination }
                    elseif ([IO.File]::Exists($destination)) { [IO.File]::Delete($destination) }
                } catch { $errors += $_ }
            }
        }
    }
    if ($errors.Count -eq 0 -and $state.Work -and [IO.Directory]::Exists($state.Work)) {
        try { [IO.Directory]::Delete($state.Work, $true) } catch { $errors += $_ }
    }
    if (-not $state.Success -and -not $state.Owned) {
        foreach ($directory in @($state.Private, $state.Install)) {
            if (($directory -ceq $state.Install -and -not $state.Created) -or -not [IO.Directory]::Exists($directory)) { continue }
            try { if ([IO.Directory]::GetFileSystemEntries($directory).Length -eq 0) { [IO.Directory]::Delete($directory) } } catch { $errors += $_ }
        }
    }
    if ($errors.Count -gt 0) { 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Private + '.' }
}
function Invoke-InstallerInstall {
    $state.Phase = 'staging'
    $state.Work = Join-Path $state.Private ('temporary-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($state.Work) | Out-Null
    foreach ($name in @('owner','current','inventory','path.record')) {
        $record = Join-Path $state.Private $name
        if ([IO.File]::Exists($record)) { [IO.File]::Copy($record, (Join-Path $state.Work ('old-' + $name))) }
    }
    $archive = 'opencode-mcp-' + $state.Version + '-' + $state.Rid + '.zip'
    $state.Phase = 'download'
    foreach ($name in @('SHA256SUMS', $archive)) {
        $destination = Join-Path $state.Work $name
        if ($state.Source) { [IO.File]::Copy((Join-Path $state.Source $name), $destination) }
        else {
            if ([Net.ServicePointManager]::SecurityProtocol -ne [Net.SecurityProtocolType]::SystemDefault -and ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12) -eq 0) {
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            }
            Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/opencode-dotnet/opencode-mcp/releases/download/v' + $state.Version + '/' + $name) -OutFile $destination
        }
    }
    $entries = @([IO.File]::ReadAllLines((Join-Path $state.Work 'SHA256SUMS')) | Where-Object { $_.EndsWith('  ' + $archive, [StringComparison]::Ordinal) })
    if ($entries.Count -ne 1 -or $entries[0] -cnotmatch ('\A[0-9a-f]{64}  ' + [regex]::Escape($archive) + '\z')) { $state.Refused = $true; throw "opencode-mcp: Invalid checksum entry: $archive." }
    if ((Get-InstallerHash (Join-Path $state.Work $archive)) -cne $entries[0].Substring(0, 64)) { $state.Refused = $true; throw "opencode-mcp: SHA-256 mismatch: $archive." }
    $state.Phase = 'staging'
    $staging = Join-Path $state.Private ('staging-' + [guid]::NewGuid().ToString('N'))
    $state.Staging = $staging
    Expand-Archive -LiteralPath (Join-Path $state.Work $archive) -DestinationPath $staging
    if (-not [IO.File]::Exists((Join-Path $staging 'opencode-mcp.exe'))) { throw 'Native command is absent.' }
    $inventory = @(Get-InstallerInventory $staging)
    if (($inventory -join "`n") -cne (@(Get-InstallerInventory $staging) -join "`n")) { throw 'Staged inventory changed.' }
    $same = $state.Owned -and $state.OldVersion -ceq $state.Version -and $state.OldRid -ceq $state.Rid
    if ($same -and ($inventory -join "`n") -cne (@(Get-InstallerRecord 'inventory') -join "`n")) { $state.Refused = $true; throw 'opencode-mcp: Installed files differ from the ownership record: inventory.' }
    $state.Phase = 'activation'
    if (-not $same) {
        $current = Join-Path $state.Install 'current'
        if ($state.Owned) {
            $previous = Join-Path $state.Private ('previous-' + [guid]::NewGuid().ToString('N'))
            [IO.Directory]::Move($current, $previous)
            $state.Previous = $previous
        }
        [IO.Directory]::Move($staging, $current)
        $state.Staging = $null
        $state.Activated = $true
    }
    $encoding = New-Object Text.UTF8Encoding $false
    $created = if ($state.Created) { '1' } else { '0' }
    [IO.File]::WriteAllLines((Join-Path $state.Work 'owner'), @('opencode-mcp', $state.Install, $created), $encoding)
    [IO.File]::WriteAllLines((Join-Path $state.Work 'current'), @($state.Version, $state.Rid), $encoding)
    [IO.File]::WriteAllLines((Join-Path $state.Work 'inventory'), $inventory, $encoding)
    $state.RecordsStarted = $true
    foreach ($name in @('owner','current','inventory')) {
        $source = Join-Path $state.Work $name
        if ([IO.File]::Exists($source)) { Invoke-InstallerRecordReplace $source (Join-Path $state.Private $name) }
    }
    $state.Phase = 'path'
    Invoke-InstallerPathAddition
    $pathRecord = Join-Path $state.Work 'path.record'
    if ([IO.File]::Exists($pathRecord)) { Invoke-InstallerRecordReplace $pathRecord (Join-Path $state.Private 'path.record') }
    $state.Success = $true
    $state.Phase = 'cleanup'
    if ($state.Previous) { $state.Retained = $state.Previous; [IO.Directory]::Delete($state.Previous, $true); $state.Previous = $null }
    if ($same) { $state.Message = "Already installed $($state.Version) ($($state.Rid)): $($state.Command)" }
    else { $state.Message = "Installed $($state.Version) ($($state.Rid)): $($state.Command)" }
    $state.PathMessage = if ($state.PathChanged) { 'PATH updated for future login sessions.' } else { 'PATH unchanged.' }
}
function Invoke-InstallerUninstall {
    if (-not $state.Owned) { $state.Message = "No owned installation: $($state.Install)"; $state.Success = $true; return }
    $state.Phase = 'uninstall'
    $current = Join-Path $state.Install 'current'
    $removing = Join-Path $state.Private ('removing-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::Move($current, $removing)
    $state.Phase = 'path'
    try { Invoke-InstallerPathRemoval }
    catch {
        $state.Refused = $false
        try { [IO.Directory]::Move($removing, $current) }
        catch { $state.Removing = $removing }
        throw
    }
    $state.Success = $true
    $state.Phase = 'cleanup'
    $state.Retained = $state.Private
    [IO.File]::Delete((Join-Path $state.Private 'path.record'))
    foreach ($name in @('owner','current','inventory')) { [IO.File]::Delete((Join-Path $state.Private $name)) }
    $state.Retained = $removing
    [IO.Directory]::Delete($removing, $true)
    $state.Retained = $state.Private
    if ([IO.Directory]::GetFileSystemEntries($state.Private).Length -eq 0) { [IO.Directory]::Delete($state.Private) }
    $state.Retained = $state.Install
    if ($state.Created -and [IO.Directory]::GetFileSystemEntries($state.Install).Length -eq 0) { [IO.Directory]::Delete($state.Install) }
    $state.Success = $true
    $state.Message = "Uninstalled opencode-mcp: $($state.Install)"
}
$OpenCodeMcpReleaseVersion = '0.1.0-preview.1'
    $state = @{
        Phase = 'staging'; Success = $false; Owned = $false; Created = $false; Refused = $false
        Activated = $false; RecordsStarted = $false; Previous = $null; Work = $null; Staging = $null
        PathWritten = $false; PathChanged = $false; PathOwned = $false; Private = $null; Install = $null
    }
    $failure = $null; $code = 0; $cleanup = @()
    try {
        if ($Options.ContainsKey('Uninstall') -and $Options.Uninstall -and ($Options.ContainsKey('Version') -or $Options.ContainsKey('AssetDir') -or $Options.ContainsKey('AddToPath'))) { $state.Refused = $true; throw 'opencode-mcp: Uninstall accepts only the installation directory.' }
        $state.Version = if ($Options.ContainsKey('Version')) { $Options.Version } else { $OpenCodeMcpReleaseVersion }
        if (-not (Test-InstallerVersion $state.Version)) { $state.Refused = $true; throw 'opencode-mcp: Version must be an exact SemVer 2.0.0 value without a leading v.' }
        $directory = if ($Options.ContainsKey('InstallDir')) { $Options.InstallDir } else { Join-Path $env:LOCALAPPDATA 'Programs/opencode-mcp' }
        $state.Install = Get-InstallerPath $directory '-InstallDir'
        $state.Private = Join-Path $state.Install '.opencode-mcp'
        $state.Command = Join-Path $state.Install 'current/opencode-mcp.exe'
        $state.AddPath = $Options.ContainsKey('AddToPath') -and $Options.AddToPath
        $state.Source = if ($Options.ContainsKey('AssetDir')) { Get-InstallerPath $Options.AssetDir '-AssetDir' } else { $null }
        if ($state.AddPath -and $state.Install.Contains(';')) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
        if ($env:OS -cne 'Windows_NT') { $state.Refused = $true; throw "opencode-mcp: Unsupported platform: $([Environment]::OSVersion.Platform)/$env:PROCESSOR_ARCHITECTURE." }
        if ([IntPtr]::Size -ne 8 -or $env:PROCESSOR_ARCHITECTURE -ieq 'x86') { $state.Refused = $true; throw 'opencode-mcp: A 64-bit operating system and shell process are required.' }
        $architecture = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
        switch ($architecture) {
            'AMD64' { $state.Rid = 'win-x64' }
            'ARM64' { $state.Rid = 'win-arm64' }
            default { $state.Refused = $true; throw "opencode-mcp: Unsupported platform: Windows/$architecture." }
        }
        if (-not $Options.Uninstall) {
            foreach ($name in @('Expand-Archive','Get-FileHash')) {
                if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { $state.Refused = $true; throw "opencode-mcp: Required command is unavailable: $name." }
            }
            if (-not $state.Source -and -not (Get-Command Invoke-WebRequest -ErrorAction SilentlyContinue)) { $state.Refused = $true; throw 'opencode-mcp: Required command is unavailable: Invoke-WebRequest.' }
        }
        if (Test-Path -LiteralPath $state.Install) {
            $item = Get-Item -LiteralPath $state.Install -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Install)." }
            $state.Install = Get-InstallerPath $item.FullName '-InstallDir'
            $state.Private = Join-Path $state.Install '.opencode-mcp'
            $state.Command = Join-Path $state.Install 'current/opencode-mcp.exe'
        }
        $state.Created = -not [IO.Directory]::Exists($state.Install)
        Get-InstallerOwnership
        Get-InstallerPathOwnership
        if ($Options.Uninstall) { Invoke-InstallerUninstall }
        else {
            if (-not $state.Owned -and (Test-Path -LiteralPath (Join-Path $state.Install 'current'))) { $state.Refused = $true; throw "opencode-mcp: Install collision: $(Join-Path $state.Install 'current')." }
            $resolved = @(Get-Command opencode-mcp -All -ErrorAction SilentlyContinue)
            foreach ($command in $resolved) {
                if (-not $state.Owned -or $command.Source -ine $state.Command) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($command.Source)." }
            }
            Invoke-InstallerInstall
        }
    } catch {
        if ($state.Refused) { $code = 2; $failure = $_.Exception.Message }
        elseif ($state.Success -and $state.Phase -eq 'cleanup') { $code = 1; $failure = 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Retained + '.' }
        else { $code = 1; $failure = 'opencode-mcp: Installation failed during ' + $state.Phase + '.' }
    } finally {
        if ($state.Success -and -not $failure -and $state.Previous -and [IO.Directory]::Exists($state.Previous)) {
            $cleanup += 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Previous + '.'
        }
        if ($state.Private) { $cleanup += @(Invoke-InstallerCleanup) }
        if ($state.Removing) { $cleanup += 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Removing + '.' }
        if ($state.Staging -and [IO.Directory]::Exists($state.Staging)) {
            try { [IO.Directory]::Delete($state.Staging, $true) }
            catch { $cleanup += 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Staging + '.' }
        }
    }
    if ($cleanup.Count -gt 0 -and $code -eq 0) { $code = 1 }
    if ($code -ne 0) {
        $messages = @()
        if ($failure) { $messages += $failure }
        $messages += $cleanup
        if ($FileInvocation) { foreach ($message in $messages) { [Console]::Error.WriteLine($message) }; exit $code }
        throw ($messages -join "`n")
    }
    Write-InstallerMessage $state.Message
    if ($state.PathMessage) { Write-InstallerMessage $state.PathMessage }
    if ($FileInvocation) { exit 0 }
}
Invoke-OpenCodeMcpInstaller @args
