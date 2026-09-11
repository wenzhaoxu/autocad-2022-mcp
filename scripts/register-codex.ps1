[CmdletBinding()]
param(
    [string]$Name = "autocad-2024"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $projectRoot "dist\src\index.js"
if (-not (Test-Path -LiteralPath $entry)) { throw "MCP build not found. Run npm run build first." }

$codex = Get-Command codex -ErrorAction Stop
& $codex.Source mcp add $Name -- node $entry
if ($LASTEXITCODE -ne 0) { throw "codex mcp add failed with exit code $LASTEXITCODE" }
Write-Output "Registered MCP server: $Name"
