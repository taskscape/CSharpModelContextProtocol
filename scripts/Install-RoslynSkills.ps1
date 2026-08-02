[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Codex', 'Claude', 'Both')]
    [string] $Client = 'Both',

    [ValidateSet('User', 'Project')]
    [string] $Scope = 'User',

    [string] $ProjectPath,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot '.agents\skills'

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
    $resolvedProjectPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
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
foreach ($target in $targets)
{
    if ($PSCmdlet.ShouldProcess($target.Root, "Install $($sourceSkills.Count) CSharpMCP skills for $($target.Client)"))
    {
        New-Item -ItemType Directory -Force -Path $target.Root | Out-Null

        foreach ($sourceSkill in $sourceSkills)
        {
            $destinationSkill = Join-Path $target.Root $sourceSkill.Name
            if ((Test-Path -LiteralPath $destinationSkill) -and -not $Force)
            {
                throw "Skill '$destinationSkill' already exists. Rerun with -Force to update it without deleting unrelated files."
            }

            New-Item -ItemType Directory -Force -Path $destinationSkill | Out-Null
            Copy-Item -Path (Join-Path $sourceSkill.FullName '*') -Destination $destinationSkill -Recurse -Force
        }

        Write-Host "Installed $($sourceSkills.Count) skills for $($target.Client) at '$($target.Root)'."
    }
}

Write-Host 'Restart the active Codex or Claude Code session so the installed skill catalog is rediscovered.'
