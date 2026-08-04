[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Codex', 'Claude', 'Both')]
    [string] $Client = 'Both',

    [ValidateSet('User', 'Project')]
    [string] $Scope = 'User',

    [string] $ProjectPath,

    [string] $UserProfilePath,

    [switch] $Force,

    [switch] $SkipExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot '.agents\skills'

if ($Force -and $SkipExisting)
{
    throw 'Force and SkipExisting cannot be used together.'
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container))
{
    throw "Canonical skills were not found at '$sourceRoot'."
}

if ($Scope -eq 'Project')
{
    if ([string]::IsNullOrWhiteSpace($ProjectPath))
    {
        throw 'ProjectPath is required when Scope is Project.'
    }

    $resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
    if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Container))
    {
        throw "ProjectPath '$resolvedProjectPath' is not a directory."
    }
}
else
{
    $resolvedProjectPath = if ([string]::IsNullOrWhiteSpace($UserProfilePath))
    {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    }
    else
    {
        [System.IO.Path]::GetFullPath($UserProfilePath)
    }
}

$targets = [System.Collections.Generic.List[object]]::new()
if ($Client -in @('Codex', 'Both'))
{
    $targets.Add([pscustomobject]@{
        Client = 'Codex'
        Root = Join-Path $resolvedProjectPath '.agents\skills'
    })
}

if ($Client -in @('Claude', 'Both'))
{
    $targets.Add([pscustomobject]@{
        Client = 'Claude'
        Root = Join-Path $resolvedProjectPath '.claude\skills'
    })
}

$sourceSkills = @(Get-ChildItem -LiteralPath $sourceRoot -Directory | Sort-Object Name)
foreach ($sourceSkill in $sourceSkills)
{
    $sourceEntryPoint = Join-Path $sourceSkill.FullName 'SKILL.md'
    if (-not (Test-Path -LiteralPath $sourceEntryPoint -PathType Leaf))
    {
        throw "Canonical skill '$($sourceSkill.Name)' has no SKILL.md entry point."
    }
}

$summaries = [System.Collections.Generic.List[object]]::new()
foreach ($target in $targets)
{
    if ($PSCmdlet.ShouldProcess($target.Root, "Install $($sourceSkills.Count) CSharpMCP skills for $($target.Client)"))
    {
        New-Item -ItemType Directory -Force -Path $target.Root | Out-Null

        $installedCount = 0
        $preservedCount = 0

        foreach ($sourceSkill in $sourceSkills)
        {
            $destinationSkill = Join-Path $target.Root $sourceSkill.Name
            $destinationEntryPoint = Join-Path $destinationSkill 'SKILL.md'
            if ((Test-Path -LiteralPath $destinationEntryPoint -PathType Leaf) -and -not $Force)
            {
                if ($SkipExisting)
                {
                    Write-Verbose "Preserved existing $($target.Client) skill '$($sourceSkill.Name)'."
                    $preservedCount++
                    continue
                }

                throw "Skill '$destinationSkill' already exists. Rerun with -Force to update it without deleting unrelated files."
            }

            # A directory without SKILL.md is not discoverable as an Agent Skill. Populate it while preserving unrelated files.
            New-Item -ItemType Directory -Force -Path $destinationSkill | Out-Null
            Copy-Item -Path (Join-Path $sourceSkill.FullName '*') -Destination $destinationSkill -Recurse -Force
            $installedCount++
        }

        Write-Host "Completed $($target.Client) skills at '$($target.Root)': installed $installedCount, preserved $preservedCount."
        $summaries.Add([pscustomobject]@{
            Client = $target.Client
            Root = $target.Root
            Installed = $installedCount
            Preserved = $preservedCount
            Total = $sourceSkills.Count
        })
    }
}

Write-Host 'Restart the active Codex or Claude Code session so the installed skill catalog is rediscovered.'
Write-Output @($summaries)
