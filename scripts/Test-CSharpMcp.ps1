[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'CSharpMCP.slnx'

function Invoke-DotNet
{
    param(
        [Parameter(Mandatory)]
        [string[]] $DotNetArguments
    )

    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet $($DotNetArguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try
{
    Write-Host 'Validating portable Codex and Claude skill coverage...'
    & (Join-Path $PSScriptRoot 'Test-RoslynSkills.ps1')
    if (-not $?)
    {
        throw 'Roslyn skill validation failed.'
    }

    Write-Host 'Restoring solution packages...'
    Invoke-DotNet -DotNetArguments @('restore', $solutionPath)

    Write-Host 'Restoring the checked-in ApiCompat tool...'
    Invoke-DotNet -DotNetArguments @('tool', 'restore')

    Write-Host 'Verifying source formatting and analyzer rules...'
    Invoke-DotNet -DotNetArguments @('format', $solutionPath, '--no-restore', '--verify-no-changes', '--verbosity', 'minimal')

    Write-Host 'Building the Release solution with deterministic single-worker settings...'
    Invoke-DotNet -DotNetArguments @(
        'build', $solutionPath,
        '--configuration', 'Release',
        '--no-restore',
        '-m:1',
        '/p:BuildInParallel=false',
        '/p:UseSharedCompilation=false'
    )

    Write-Host 'Running all Roslyn behavior and MCP stdio protocol tests...'
    Invoke-DotNet -DotNetArguments @(
        'test', $solutionPath,
        '--configuration', 'Release',
        '--no-build',
        '-m:1',
        '/p:BuildInParallel=false',
        '/p:UseSharedCompilation=false',
        '--logger', 'console;verbosity=normal'
    )

    Write-Host 'CSharpMCP verification completed successfully.'
}
finally
{
    Pop-Location
}
