[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $StatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf))
{
    Write-Host 'No installer registration state exists; no MCP client registrations will be removed.'
    exit 0
}

$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$serverPath = [System.IO.Path]::GetFullPath([string] $state.ServerPath)

function Find-ClientCommand
{
    param([Parameter(Mandatory)][string] $Name)

    return Get-Command -Name $Name -All -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandType -in @('Application', 'ExternalScript') } |
        Select-Object -First 1
}

foreach ($registration in @($state.Registrations))
{
    if (-not [bool] $registration.InstalledByInstaller -or $registration.Client -notin @('Codex', 'Claude'))
    {
        continue
    }

    $commandName = if ($registration.Client -eq 'Codex') { 'codex' } else { 'claude' }
    $command = Find-ClientCommand -Name $commandName
    if ($null -eq $command)
    {
        Write-Host "$($registration.Client): client command is unavailable; registration was preserved."
        continue
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try
    {
        $currentOutput = @(& $command.Source mcp get csharp_roslyn 2>&1 | ForEach-Object { $_.ToString() })
        $getExitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($getExitCode -ne 0 -or ($currentOutput -join [Environment]::NewLine).IndexOf($serverPath, [StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        Write-Host "$($registration.Client): registration no longer points to the installed server; it was preserved."
        continue
    }

    if ($PSCmdlet.ShouldProcess($registration.Client, 'Remove installer-owned csharp_roslyn registration'))
    {
        & $command.Source mcp remove csharp_roslyn
        if ($LASTEXITCODE -eq 0)
        {
            Write-Host "$($registration.Client): removed installer-owned csharp_roslyn registration."
        }
        else
        {
            Write-Warning "$($registration.Client): registration removal failed with exit code $LASTEXITCODE."
        }
    }
}

# Installed skills are intentionally retained because users may have modified or referenced them after installation.
exit 0
