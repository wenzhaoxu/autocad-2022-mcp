[CmdletBinding()]
param(
    [string]$BuildConfiguration = "Release",
    [string]$PluginPath
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceDll = if ($PluginPath) { [System.IO.Path]::GetFullPath($PluginPath) } else { Join-Path $projectRoot "plugin\AutoCAD2024Mcp\bin\$BuildConfiguration\AutoCAD2024Mcp.dll" }
$manifest = Join-Path $projectRoot "bundle\AutoCAD2024Mcp.bundle\PackageContents.xml"
$pluginsRoot = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
$bundleRoot = Join-Path $pluginsRoot "AutoCAD2024Mcp.bundle"
$contentsRoot = Join-Path $bundleRoot "Contents\Windows"

if (-not (Test-Path -LiteralPath $sourceDll)) {
    throw "Plugin build not found: $sourceDll. Build AutoCAD2024Mcp.csproj first."
}

$manifestText = Get-Content -LiteralPath $manifest -Raw
if ($manifestText -notmatch 'AppVersion="0\.0\.3"' -or $manifestText -notmatch 'AutoCAD2024Mcp\.0\.0\.3\.dll') {
    throw "Refusing to install: bundle manifest is not MCP 0.0.3."
}
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($sourceDll).Version
if ($assemblyVersion -ne [Version]"0.0.3.0") {
    throw "Refusing to install assembly version $assemblyVersion; expected 0.0.3.0."
}

New-Item -ItemType Directory -Force -Path $contentsRoot | Out-Null
Copy-Item -LiteralPath $manifest -Destination (Join-Path $bundleRoot "PackageContents.xml") -Force
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $contentsRoot "AutoCAD2024Mcp.0.0.3.dll") -Force

$installedDll = Join-Path $contentsRoot "AutoCAD2024Mcp.0.0.3.dll"
if ((Get-FileHash -LiteralPath $sourceDll).Hash -ne (Get-FileHash -LiteralPath $installedDll).Hash) {
    throw "Installed plugin hash does not match the release binary."
}

Write-Output "Installed AutoCAD MCP 0.0.3: $bundleRoot"
Write-Output "Restart AutoCAD 2024, then run CADMCPSTATUS."
