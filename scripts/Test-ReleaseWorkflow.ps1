[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionPath = Join-Path $repositoryRoot 'VERSION'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\publish-installer.yml'
$buildScriptPath = Join-Path $repositoryRoot 'scripts\Build-Installer.ps1'
$packageTestPath = Join-Path $repositoryRoot 'scripts\Test-InstallerPackage.ps1'

foreach ($requiredPath in @($versionPath, $workflowPath, $buildScriptPath, $packageTestPath))
{
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf))
    {
        throw "Release workflow asset '$requiredPath' is missing."
    }
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "VERSION '$version' is not a stable three-component release version."
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredWorkflowEvidence = @(
    'branches:',
    '- main',
    'permissions:',
    'contents: write',
    'runs-on: windows-2025',
    'actions/checkout@v6',
    'actions/setup-dotnet@v5',
    'global-json-file: global.json',
    './scripts/Build-Installer.ps1 -Version',
    'Get-FileHash',
    'gh release create',
    'gh release upload',
    'gh release edit',
    'CSharpMCP-${{ steps.version.outputs.version }}-win-x64-Setup.exe.sha256'
)

foreach ($evidence in $requiredWorkflowEvidence)
{
    if ($workflow.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0)
    {
        throw "Release workflow is missing required evidence '$evidence'."
    }
}

$buildScript = Get-Content -LiteralPath $buildScriptPath -Raw
$packageTest = Get-Content -LiteralPath $packageTestPath -Raw
if ($buildScript.IndexOf("Join-Path `$repositoryRoot 'VERSION'", [StringComparison]::Ordinal) -lt 0)
{
    throw 'Build-Installer.ps1 does not default to the repository VERSION file.'
}

if ($packageTest.IndexOf("Join-Path `$repositoryRoot 'VERSION'", [StringComparison]::Ordinal) -lt 0)
{
    throw 'Test-InstallerPackage.ps1 does not derive its default artifact name from VERSION.'
}

Write-Host "Validated main-branch release publishing for CSharpMCP $version."
