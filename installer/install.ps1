<#
  Installs or removes the SpaceMouse plugin for BrickLink Studio 2.0.

  Part 1 (no admin rights needed): copy the plugin to %LOCALAPPDATA%\SpaceMouseStudio.
  Part 2 (admin rights needed): copy the loader to Studio_Data\Managed and add it to
  ScriptingAssemblies.json and RuntimeInitializeOnLoads.json, so that Unity loads it when Studio
  starts. The original files are backed up as *.orig before the first change.

  If the script is not running as administrator, it starts itself a second time elevated with
  -AdminPart for part 2 (Windows shows a UAC prompt). Errors from that elevated run are passed
  back through -ErrorFile.

  Usage:
    install.ps1                          install
    install.ps1 -Uninstall               remove
    install.ps1 -StudioDir "D:\Studio"   if Studio is not found automatically
#>
param(
    [switch]$Uninstall,
    [string]$StudioDir = "",
    [switch]$AdminPart,
    [string]$ErrorFile = ""
)

$ErrorActionPreference = 'Stop'
$LoaderName = 'SpaceMouseStudio.Loader'
$Root = Split-Path -Parent $PSScriptRoot
$Bin = Join-Path $Root 'bin'
$UserDir = Join-Path $env:LOCALAPPDATA 'SpaceMouseStudio'

# Windows PowerShell 5.1 would otherwise serialize arrays as {"value":[...],"Count":n}
Remove-TypeData -TypeName System.Array -ErrorAction SilentlyContinue

# Finds Studio's installation folder: -StudioDir, the uninstall entries in the registry, or the default paths.
function Find-StudioDir {
    if ($StudioDir) { return $StudioDir }
    $candidates = @('C:\Program Files\Studio 2.0', 'C:\Program Files (x86)\Studio 2.0')
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    foreach ($entry in Get-ItemProperty $keys -ErrorAction SilentlyContinue) {
        if ($entry.DisplayName -like 'Studio 2.0*' -and $entry.InstallLocation) { $candidates = @($entry.InstallLocation) + $candidates }
    }
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'Studio_Data\ScriptingAssemblies.json')) { return $dir.TrimEnd('\') }
    }
    throw 'BrickLink Studio 2.0 was not found. Specify the folder with: install.ps1 -StudioDir "C:\...\Studio 2.0"'
}

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# True if a Studio process from this installation folder is running.
function Test-StudioRunning([string]$dir) {
    $running = Get-Process -Name Studio -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) }
    return [bool]$running
}

function Read-Json([string]$path) {
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
}

# Unity expects UTF-8 without BOM.
function Write-Json([string]$path, $object) {
    $json = $object | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false))
}

# Admin part of the installation. Safe to run repeatedly: entries are only added if missing.
function Install-StudioPart([string]$dir) {
    $data = Join-Path $dir 'Studio_Data'
    $managed = Join-Path $data 'Managed'
    $assembliesFile = Join-Path $data 'ScriptingAssemblies.json'
    $initFile = Join-Path $data 'RuntimeInitializeOnLoads.json'
    $source = Join-Path $Bin "$LoaderName.dll"
    $target = Join-Path $managed "$LoaderName.dll"

    # a running Studio keeps the loader DLL locked
    if ((Test-Path $target) -and ((Get-FileHash $source).Hash -ne (Get-FileHash $target).Hash) -and (Test-StudioRunning $dir)) {
        throw 'Studio is running and uses the old loader. Please close Studio and install again.'
    }
    foreach ($file in $assembliesFile, $initFile) {
        if (-not (Test-Path "$file.orig")) { Copy-Item $file "$file.orig" }
    }
    Copy-Item $source $target -Force

    # ScriptingAssemblies.json: two parallel lists of assembly names and types (16 = user assembly)
    $assemblies = Read-Json $assembliesFile
    if (@($assemblies.names) -notcontains "$LoaderName.dll") {
        $assemblies.names = @($assemblies.names) + "$LoaderName.dll"
        $assemblies.types = @($assemblies.types) + 16
        Write-Json $assembliesFile $assemblies
    }

    # RuntimeInitializeOnLoads.json: methods Unity calls at startup ([RuntimeInitializeOnLoadMethod])
    $init = Read-Json $initFile
    if (-not (@($init.root) | Where-Object { $_.assemblyName -eq $LoaderName })) {
        $entry = [pscustomobject][ordered]@{
            assemblyName = $LoaderName; nameSpace = 'SpaceMouseStudio'; className = 'Loader'
            methodName = 'Boot'; loadTypes = 0; isUnityClass = $false
        }
        $init.root = @($init.root) + $entry
        Write-Json $initFile $init
    }

    # sanity check: both files must still be valid JSON and the two lists equally long
    $check = Read-Json $assembliesFile
    if (@($check.names).Count -ne @($check.types).Count) { throw 'ScriptingAssemblies.json is inconsistent - please restore the *.orig files.' }
    $null = Read-Json $initFile
}

# Admin part of the removal: takes out exactly the entries Install-StudioPart added.
function Uninstall-StudioPart([string]$dir) {
    $data = Join-Path $dir 'Studio_Data'
    $assembliesFile = Join-Path $data 'ScriptingAssemblies.json'
    $initFile = Join-Path $data 'RuntimeInitializeOnLoads.json'
    $target = Join-Path $data "Managed\$LoaderName.dll"

    $assemblies = Read-Json $assembliesFile
    $index = [Array]::IndexOf(@($assemblies.names), "$LoaderName.dll")
    if ($index -ge 0) {
        $names = New-Object System.Collections.ArrayList(, @($assemblies.names))
        $types = New-Object System.Collections.ArrayList(, @($assemblies.types))
        $names.RemoveAt($index)
        $types.RemoveAt($index)
        $assemblies.names = $names.ToArray()
        $assemblies.types = $types.ToArray()
        Write-Json $assembliesFile $assemblies
    }
    $init = Read-Json $initFile
    $remaining = @(@($init.root) | Where-Object { $_.assemblyName -ne $LoaderName })
    if ($remaining.Count -ne @($init.root).Count) {
        $init.root = $remaining
        Write-Json $initFile $init
    }
    if (Test-Path $target) {
        if (Test-StudioRunning $dir) { throw 'Studio is still running. Please close Studio and uninstall again.' }
        Remove-Item $target -Force
    }
}

try {
    $dir = Find-StudioDir

    # elevated second run: do only the admin part and report errors through $ErrorFile
    if ($AdminPart) {
        if ($Uninstall) { Uninstall-StudioPart $dir } else { Install-StudioPart $dir }
        exit 0
    }

    Write-Host "BrickLink Studio found: $dir"
    if (-not $Uninstall) {
        New-Item -ItemType Directory -Force $UserDir | Out-Null
        Copy-Item (Join-Path $Bin 'SpaceMouseStudio.dll') $UserDir -Force
        Write-Host "Plugin copied to $UserDir"
    }

    if (Test-Admin) {
        if ($Uninstall) { Uninstall-StudioPart $dir } else { Install-StudioPart $dir }
    } else {
        Write-Host 'Administrator rights are needed to register the loader with Studio (Windows will ask) ...'
        $errorFile = Join-Path $env:TEMP 'SpaceMouseStudio-install-error.txt'
        Remove-Item $errorFile -ErrorAction SilentlyContinue
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-AdminPart',
                       '-StudioDir', "`"$dir`"", '-ErrorFile', "`"$errorFile`"")
        if ($Uninstall) { $arguments += '-Uninstall' }
        $process = Start-Process powershell -Verb RunAs -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0) {
            $detail = if (Test-Path $errorFile) { Get-Content $errorFile -Raw -Encoding UTF8 } else { "code $($process.ExitCode)" }
            throw "The administrator step failed: $detail"
        }
    }

    if ($Uninstall) {
        Remove-Item (Join-Path $UserDir 'SpaceMouseStudio.dll') -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Host 'Uninstalled. The settings in' $UserDir 'are kept.'
    } else {
        Write-Host ''
        Write-Host 'Done. The SpaceMouse will be active the next time Studio starts.'
        Write-Host "Settings: $UserDir\config.ini   Log: $UserDir\log.txt"
        if (Test-StudioRunning $dir) { Write-Host 'Studio is running - please restart it once.' }
    }
    exit 0
} catch {
    if ($AdminPart -and $ErrorFile) {
        [IO.File]::WriteAllText($ErrorFile, $_.Exception.Message, (New-Object Text.UTF8Encoding $false))
    }
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
