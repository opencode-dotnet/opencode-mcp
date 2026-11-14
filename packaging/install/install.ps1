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
    if ($selection.Count -notin @(2,3) -or -not (Test-InstallerVersion $selection[0]) -or $selection[1] -notin @('win-x64','win-arm64') -or ($selection.Count -eq 3 -and $selection[2] -cnotmatch '\A[0-9a-f]{64}\z')) { $state.Refused = $true; throw "opencode-mcp: Install collision: $($state.Private)." }
    $state.OldVersion = $selection[0]; $state.OldRid = $selection[1]
    $state.OldArchiveHash = if ($selection.Count -eq 3) { $selection[2] } else { $null }
    $state.Created = $owner[2] -ceq '1'
    foreach ($entry in Get-ChildItem -LiteralPath $state.Private -Force) {
        if ($entry.Name -cnotin @('owner','current','inventory','path.record')) { $state.Refused = $true; throw "opencode-mcp: Installed files differ from the ownership record: .opencode-mcp/$($entry.Name)." }
    }
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
    if ($record.Count -ne 2 -or -not [string]::Equals($record[0], $token, [StringComparison]::OrdinalIgnoreCase) -or $record[1] -notin @('absent','present')) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
    $current = Get-InstallerUserPath
    $tokens = @(([string]$current.Value).Split(';'))
    if (@($tokens | Where-Object { [string]::Equals($_, $token, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) { $state.Refused = $true; throw 'opencode-mcp: PATH ownership is ambiguous: user-Path.' }
    $state.PathOwned = $true
    $state.PathOriginallyAbsent = $record[1] -ceq 'absent'
}
function Invoke-InstallerPathRemoval {
    if (-not $state.PathOwned) { return }
    Get-InstallerPathOwnership
    $current = Get-InstallerUserPath
    $token = Join-Path $state.Install 'current'
    $tokens = @(([string]$current.Value).Split(';'))
    $remaining = @($tokens | Where-Object { -not [string]::Equals($_, $token, [StringComparison]::OrdinalIgnoreCase) })
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
function Test-InstallerCommandAccess {
    $handle = [IO.File]::Open($state.Command, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $handle.Dispose()
}
function Invoke-InstallerDirectoryOperation($Source, $Destination, $Recursive = $false) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    for ($attempt = 0; ; $attempt++) {
        try {
            if ($Destination) { [IO.Directory]::Move($Source, $Destination) }
            else { [IO.Directory]::Delete($Source, $Recursive) }
            return
        } catch {
            $cause = $_.Exception.GetBaseException()
            if (($cause -isnot [IO.IOException] -and $cause -isnot [UnauthorizedAccessException]) -or $cause.HResult -notin @(-2147024864, -2147024891) -or $attempt -ge 39 -or $clock.ElapsedMilliseconds -ge 9800) { throw }
            Start-Sleep -Milliseconds 200
            if ($clock.ElapsedMilliseconds -ge 10000) { throw }
        }
    }
}
function Invoke-InstallerCleanup {
    if (-not $state.Work) { return }
    $errors = @()
    $retained = $state.Private
    $restoreRecords = $true
    if (-not $state.Success) {
        if ($state.PathWritten) {
            try {
                # Reverse only our inserted token from the latest raw value.
                $current = Get-InstallerUserPath
                $token = Join-Path $state.Install 'current'
                $tokens = @(([string]$current.Value).Split(';'))
                if (@($tokens | Where-Object { [string]::Equals($_, $token, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) { throw 'PATH rollback ownership is ambiguous.' }
                $value = @($tokens | Where-Object { -not [string]::Equals($_, $token, [StringComparison]::OrdinalIgnoreCase) }) -join ';'
                if ($state.PathOriginallyAbsent -and $value -ceq '') { $value = $null }
                Invoke-InstallerPathWrite $value $current.Kind
            } catch { $errors += $_ }
        }
        if ($state.Activated) {
            try {
                $state.Staging = Join-Path $state.Private ('removing-' + [guid]::NewGuid().ToString('N'))
                Invoke-InstallerDirectoryOperation (Join-Path $state.Install 'current') $state.Staging
            } catch { $errors += $_; $restoreRecords = $false }
        }
        if ($restoreRecords -and $state.Previous -and [IO.Directory]::Exists($state.Previous)) {
            try { Invoke-InstallerDirectoryOperation $state.Previous (Join-Path $state.Install 'current') } catch { $errors += $_; $restoreRecords = $false }
        }
        if ($state.RecordsStarted -and $restoreRecords) {
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
    if ($state.Staging -and [IO.Directory]::Exists($state.Staging)) {
        try { Invoke-InstallerDirectoryOperation -Source $state.Staging -Recursive $true } catch { $errors += $_ }
    }
    if ($errors.Count -eq 0 -and $state.Work -and [IO.Directory]::Exists($state.Work)) {
        try { Invoke-InstallerDirectoryOperation -Source $state.Work -Recursive $true } catch { $errors += $_ }
    }
    if (-not $state.Success -and -not $state.Owned) {
        foreach ($directory in @($state.Private, $state.Install)) {
            if (($directory -ceq $state.Install -and -not $state.Created) -or -not [IO.Directory]::Exists($directory)) { continue }
            try { if ([IO.Directory]::GetFileSystemEntries($directory).Length -eq 0) { Invoke-InstallerDirectoryOperation $directory } } catch { $errors += $_; $retained = $directory }
        }
    }
    if ($errors.Count -gt 0) { 'opencode-mcp: Cleanup failed; retained owned directory: ' + $retained + '.' }
}
function Invoke-InstallerDownload($Name, $Destination) {
    if ($state.Source) {
        if ($Destination) { [IO.File]::Copy((Join-Path $state.Source $Name), $Destination) }
        else { [IO.File]::ReadAllText((Join-Path $state.Source $Name)) }
    } else {
        if ([Net.ServicePointManager]::SecurityProtocol -ne [Net.SecurityProtocolType]::SystemDefault -and ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12) -eq 0) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        }
        $url = 'https://github.com/opencode-dotnet/opencode-mcp/releases/download/v' + $state.Version + '/' + $Name
        if ($Destination) { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Destination }
        else {
            $content = (Invoke-WebRequest -UseBasicParsing -Uri $url).Content
            if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { $content }
        }
    }
}
function Invoke-InstallerInstall {
    $archive = 'opencode-mcp-' + $state.Version + '-' + $state.Rid + '.zip'
    $state.Phase = 'download'
    $checksums = Invoke-InstallerDownload 'SHA256SUMS'
    $entries = @($checksums -split '\r?\n' | Where-Object { $_.EndsWith('  ' + $archive, [StringComparison]::Ordinal) })
    if ($entries.Count -ne 1 -or $entries[0] -cnotmatch ('\A[0-9a-f]{64}  ' + [regex]::Escape($archive) + '\z')) { $state.Refused = $true; throw "opencode-mcp: Invalid checksum entry: $archive." }
    $archiveHash = $entries[0].Substring(0, 64)
    if ($state.Owned -and $state.OldVersion -ceq $state.Version -and $state.OldRid -ceq $state.Rid -and $state.OldArchiveHash -ceq $archiveHash) {
        $state.Success = $true
        $state.Message = "Already installed $($state.Version) ($($state.Rid)): $($state.Command)"
        $state.PathMessage = 'PATH unchanged.'
        return
    }
    $state.Phase = 'staging'
    $state.Work = Join-Path $state.Private ('temporary-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($state.Work) | Out-Null
    foreach ($name in @('owner','current','inventory','path.record')) {
        $record = Join-Path $state.Private $name
        if ([IO.File]::Exists($record)) { [IO.File]::Copy($record, (Join-Path $state.Work ('old-' + $name))) }
    }
    $state.Phase = 'download'
    Invoke-InstallerDownload $archive (Join-Path $state.Work $archive)
    if ((Get-InstallerHash (Join-Path $state.Work $archive)) -cne $archiveHash) { $state.Refused = $true; throw "opencode-mcp: SHA-256 mismatch: $archive." }
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
            Test-InstallerCommandAccess
            $previous = Join-Path $state.Private ('previous-' + [guid]::NewGuid().ToString('N'))
            Invoke-InstallerDirectoryOperation $current $previous
            $state.Previous = $previous
        }
        Invoke-InstallerDirectoryOperation $staging $current
        $state.Staging = $null
        $state.Activated = $true
    }
    $encoding = New-Object Text.UTF8Encoding $false
    $created = if ($state.Created) { '1' } else { '0' }
    [IO.File]::WriteAllLines((Join-Path $state.Work 'owner'), @('opencode-mcp', $state.Install, $created), $encoding)
    [IO.File]::WriteAllLines((Join-Path $state.Work 'current'), @($state.Version, $state.Rid, $archiveHash), $encoding)
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
    if ($state.Previous) { $state.Retained = $state.Private; Invoke-InstallerDirectoryOperation -Source $state.Previous -Recursive $true; $state.Previous = $null }
    $state.Message = "Installed $($state.Version) ($($state.Rid)): $($state.Command)"
    $state.PathMessage = if ($state.PathChanged) { 'PATH updated for future login sessions.' } else { 'PATH unchanged.' }
}
function Invoke-InstallerUninstall {
    if (-not $state.Owned) { $state.Message = "No owned installation: $($state.Install)"; $state.Success = $true; return }
    $state.Phase = 'uninstall'
    $current = Join-Path $state.Install 'current'
    $removing = Join-Path $state.Private ('removing-' + [guid]::NewGuid().ToString('N'))
    Test-InstallerCommandAccess
    Invoke-InstallerDirectoryOperation $current $removing
    $state.Phase = 'path'
    try { Invoke-InstallerPathRemoval }
    catch {
        $state.Refused = $false
        try { Invoke-InstallerDirectoryOperation $removing $current }
        catch { $state.Removing = $removing }
        throw
    }
    $state.Success = $true
    $state.Phase = 'cleanup'
    $state.Retained = $state.Private
    [IO.File]::Delete((Join-Path $state.Private 'path.record'))
    foreach ($name in @('owner','current','inventory')) { [IO.File]::Delete((Join-Path $state.Private $name)) }
    Invoke-InstallerDirectoryOperation -Source $removing -Recursive $true
    $state.Retained = $state.Private
    if ([IO.Directory]::GetFileSystemEntries($state.Private).Length -eq 0) { Invoke-InstallerDirectoryOperation $state.Private }
    $state.Retained = $state.Install
    if ($state.Created -and [IO.Directory]::GetFileSystemEntries($state.Install).Length -eq 0) { Invoke-InstallerDirectoryOperation $state.Install }
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
            $cleanup += 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Private + '.'
        }
        if ($state.Private) { $cleanup += @(Invoke-InstallerCleanup) }
        if ($state.Removing) { $cleanup += 'opencode-mcp: Cleanup failed; retained owned directory: ' + $state.Private + '.' }
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
