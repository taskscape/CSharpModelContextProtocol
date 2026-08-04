[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $ServerPath,

    [ValidateSet('Codex', 'Claude', 'Both')]
    [string] $Client = 'Both',

    [ValidateSet('', 'api', 'architecture', 'api,architecture', 'all')]
    [string] $ToolGroups = '',

    [string] $StatePath,

    [switch] $InstallSkills,

    [string] $SkillsUserProfilePath,

    [switch] $RestoreApiCompat
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedServerPath = [System.IO.Path]::GetFullPath($ServerPath)
if (-not (Test-Path -LiteralPath $resolvedServerPath -PathType Leaf))
{
    throw "The installed MCP server does not exist at '$resolvedServerPath'."
}

if ([string]::IsNullOrWhiteSpace($StatePath))
{
    $StatePath = Join-Path (Split-Path -Parent $resolvedServerPath) '..\config\registration-state.json'
}

$resolvedStatePath = [System.IO.Path]::GetFullPath($StatePath)
$results = [System.Collections.Generic.List[object]]::new()
$registrationCmdlet = $PSCmdlet

function Find-ClientCommand
{
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    return Get-Command -Name $Name -All -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandType -in @('Application', 'ExternalScript') } |
        Select-Object -First 1
}

function Invoke-ExternalCommand
{
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.CommandInfo] $Command,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try
    {
        $commandOutput = @(& $Command.Source @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($commandOutput | ForEach-Object { $_.ToString() })
    }
}

function Register-Client
{
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Codex', 'Claude')]
        [string] $ClientName
    )

    $commandName = if ($ClientName -eq 'Codex') { 'codex' } else { 'claude' }
    $command = Find-ClientCommand -Name $commandName
    if ($null -eq $command)
    {
        return [pscustomobject]@{
            Client = $ClientName
            CommandFound = $false
            Status = 'client-not-found'
            InstalledByInstaller = $false
            Details = "$commandName was not found on PATH; registration was skipped."
        }
    }

    $existing = Invoke-ExternalCommand -Command $command -Arguments @('mcp', 'get', 'csharp_roslyn')
    if ($existing.ExitCode -eq 0)
    {
        return [pscustomobject]@{
            Client = $ClientName
            CommandFound = $true
            Status = 'already-configured'
            InstalledByInstaller = $false
            Details = 'An existing csharp_roslyn registration was preserved unchanged.'
        }
    }

    $arguments = if ($ClientName -eq 'Codex')
    {
        @('mcp', 'add', 'csharp_roslyn')
    }
    else
    {
        @('mcp', 'add', '--transport', 'stdio', '--scope', 'user')
    }

    if (-not [string]::IsNullOrWhiteSpace($ToolGroups))
    {
        $arguments += @('--env', "CSHARPMCP_TOOL_GROUPS=$ToolGroups")
    }

    if ($ClientName -eq 'Claude')
    {
        $arguments += 'csharp_roslyn'
    }

    $arguments += @('--', $resolvedServerPath)
    if (-not $registrationCmdlet.ShouldProcess($ClientName, "Register csharp_roslyn using '$resolvedServerPath'"))
    {
        return [pscustomobject]@{
            Client = $ClientName
            CommandFound = $true
            Status = 'planned'
            InstalledByInstaller = $false
            Details = 'Registration was evaluated in WhatIf mode.'
        }
    }

    $registration = Invoke-ExternalCommand -Command $command -Arguments $arguments
    if ($registration.ExitCode -eq 0)
    {
        return [pscustomobject]@{
            Client = $ClientName
            CommandFound = $true
            Status = 'registered'
            InstalledByInstaller = $true
            Details = 'The installer added the csharp_roslyn MCP registration.'
        }
    }

    return [pscustomobject]@{
        Client = $ClientName
        CommandFound = $true
        Status = 'registration-failed'
        InstalledByInstaller = $false
        Details = "Registration exited with code $($registration.ExitCode): $($registration.Output -join ' ')"
    }
}

if ($InstallSkills)
{
    $skillInstaller = Join-Path $PSScriptRoot 'Install-RoslynSkills.ps1'
    if (Test-Path -LiteralPath $skillInstaller -PathType Leaf)
    {
        try
        {
            $skillArguments = @{
                Client = 'Both'
                Scope = 'User'
                SkipExisting = $true
                WhatIf = $WhatIfPreference
            }
            if (-not [string]::IsNullOrWhiteSpace($SkillsUserProfilePath))
            {
                $skillArguments.UserProfilePath = $SkillsUserProfilePath
            }

            $skillSummary = @(& $skillInstaller @skillArguments)
            $installedSkillCount = @($skillSummary | Measure-Object -Property Installed -Sum).Sum
            $preservedSkillCount = @($skillSummary | Measure-Object -Property Preserved -Sum).Sum
            $results.Add([pscustomobject]@{
                Client = 'Skills'
                CommandFound = $true
                Status = if ($WhatIfPreference) { 'planned' } elseif ($installedSkillCount -gt 0) { 'installed' } else { 'already-installed' }
                InstalledByInstaller = -not $WhatIfPreference -and $installedSkillCount -gt 0
                Details = if ($WhatIfPreference) { 'Dual-client skill installation was evaluated in WhatIf mode.' } else { "Installed $installedSkillCount and preserved $preservedSkillCount skills across Codex and Claude Code." }
            })
        }
        catch
        {
            $results.Add([pscustomobject]@{
                Client = 'Skills'
                CommandFound = $true
                Status = 'installation-failed'
                InstalledByInstaller = $false
                Details = $_.Exception.Message
            })
        }
    }
    else
    {
        $results.Add([pscustomobject]@{
            Client = 'Skills'
            CommandFound = $false
            Status = 'installer-not-found'
            InstalledByInstaller = $false
            Details = "The packaged skill installer was not found at '$skillInstaller'."
        })
    }
}

if ($RestoreApiCompat)
{
    $dotnet = Find-ClientCommand -Name 'dotnet'
    $manifestPath = Join-Path (Split-Path -Parent $resolvedServerPath) '.config\dotnet-tools.json'
    if ($null -ne $dotnet -and (Test-Path -LiteralPath $manifestPath -PathType Leaf))
    {
        if ($registrationCmdlet.ShouldProcess('ApiCompat', "Restore the optional tool manifest '$manifestPath'"))
        {
            $restore = Invoke-ExternalCommand -Command $dotnet -Arguments @('tool', 'restore', '--tool-manifest', $manifestPath)
            $results.Add([pscustomobject]@{
                Client = 'ApiCompat'
                CommandFound = $true
                Status = if ($restore.ExitCode -eq 0) { 'restored' } else { 'restore-failed' }
                InstalledByInstaller = $restore.ExitCode -eq 0
                Details = if ($restore.ExitCode -eq 0) { 'The optional Microsoft ApiCompat tool was restored.' } else { $restore.Output -join ' ' }
            })
        }
    }
    else
    {
        $results.Add([pscustomobject]@{
            Client = 'ApiCompat'
            CommandFound = $null -ne $dotnet
            Status = 'restore-skipped'
            InstalledByInstaller = $false
            Details = 'dotnet or the packaged ApiCompat manifest was unavailable. Default MCP tools are unaffected.'
        })
    }
}

if ($Client -in @('Codex', 'Both'))
{
    $results.Add((Register-Client -ClientName Codex))
}

if ($Client -in @('Claude', 'Both'))
{
    $results.Add((Register-Client -ClientName Claude))
}

$state = [pscustomobject]@{
    ServerPath = $resolvedServerPath
    ToolGroups = $ToolGroups
    AttemptedAt = [DateTimeOffset]::Now
    Registrations = @($results)
}

if ($registrationCmdlet.ShouldProcess($resolvedStatePath, 'Write MCP client registration state'))
{
    $stateDirectory = Split-Path -Parent $resolvedStatePath
    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedStatePath -Encoding UTF8
}

foreach ($result in $results)
{
    Write-Host "$($result.Client): $($result.Status) - $($result.Details)"
}

# Client discovery or registration failure is deliberately non-fatal to the installer; the state file preserves exact outcomes for repair.
exit 0
