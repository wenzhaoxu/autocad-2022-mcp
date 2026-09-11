[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = "Stop"
$bundleRoot = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AutoCAD2024Mcp.bundle"
$pluginsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"))
$resolved = [System.IO.Path]::GetFullPath($bundleRoot)

if (-not $resolved.StartsWith($pluginsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove path outside Autodesk ApplicationPlugins: $resolved"
}
if (Test-Path -LiteralPath $resolved) {
    if ($PSCmdlet.ShouldProcess($resolved, "Remove AutoCAD MCP plugin bundle")) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Output "Removed: $resolved"
    }
} else {
    Write-Output "Plugin is not installed: $resolved"
}
