[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "0.1.1",
    [string]$DotnetExe = "dotnet"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "release-staging"))
$stage = [System.IO.Path]::GetFullPath((Join-Path $stageRoot "MCP-$Version"))
$releaseRoot = Join-Path $projectRoot "release"
$archive = Join-Path $releaseRoot "MCP-$Version.zip"
$temporaryArchive = Join-Path $releaseRoot "MCP-$Version.building.zip"

if (-not $stage.StartsWith($stageRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a staging directory outside release-staging: $stage"
}

Push-Location $projectRoot
try {
    & npm run check
    if ($LASTEXITCODE -ne 0) { throw "npm run check failed with exit code $LASTEXITCODE" }

    & $DotnetExe build "plugin\AutoCAD2024Mcp\AutoCAD2024Mcp.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE" }

    $plugin = Join-Path $projectRoot "plugin\AutoCAD2024Mcp\bin\Release\AutoCAD2024Mcp.dll"
    if (-not (Test-Path -LiteralPath $plugin)) { throw "Plugin build not found: $plugin" }
    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($plugin).Version
    if ($assemblyVersion -ne [Version]"$Version.0") {
        throw "Plugin assembly version $assemblyVersion does not match package version $Version."
    }

    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $bundle = Join-Path $stage "AutoCAD2024Mcp.bundle"
    $bundleWindows = Join-Path $bundle "Contents\Windows"
    New-Item -ItemType Directory -Path $bundleWindows -Force | Out-Null

    & npm pack --pack-destination $stage
    if ($LASTEXITCODE -ne 0) { throw "npm pack failed with exit code $LASTEXITCODE" }

    $package = Join-Path $stage "autocad-2024-mcp-$Version.tgz"
    if (-not (Test-Path -LiteralPath $package)) { throw "npm package was not created: $package" }
    Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination (Join-Path $stage "CHANGELOG.md") -Force
    Copy-Item -LiteralPath (Join-Path $releaseRoot "MCP-$Version-INSTALL.md") -Destination (Join-Path $stage "INSTALL.md") -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "bundle\AutoCAD2024Mcp.bundle\PackageContents.xml") -Destination (Join-Path $bundle "PackageContents.xml") -Force
    $versionedPlugin = Join-Path $bundleWindows "AutoCAD2024Mcp.$Version.dll"
    Copy-Item -LiteralPath $plugin -Destination $versionedPlugin -Force

    $hashTargets = @($package, $versionedPlugin, (Join-Path $bundle "PackageContents.xml"))
    $hashLines = foreach ($target in $hashTargets) {
        $relative = [System.IO.Path]::GetRelativePath($stage, $target).Replace('\', '/')
        "{0}  {1}" -f (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash, $relative
    }
    Set-Content -LiteralPath (Join-Path $stage "SHA256SUMS.txt") -Value $hashLines -Encoding ascii

    if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
    Compress-Archive -LiteralPath $stage -DestinationPath $temporaryArchive -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Select-Object Hash, Path
}
finally {
    Pop-Location
}
