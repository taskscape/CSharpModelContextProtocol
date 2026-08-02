[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolSourcePath = Join-Path $repositoryRoot 'src\CSharpMcp.Server\Tools\RoslynTools.cs'
$codexSkillsPath = Join-Path $repositoryRoot '.agents\skills'
$claudeSkillsPath = Join-Path $repositoryRoot '.claude\skills'
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError
{
    param(
        [Parameter(Mandatory)]
        [string] $Message
    )

    $errors.Add($Message)
}

function Get-SkillFrontmatter
{
    param(
        [Parameter(Mandatory)]
        [string] $Content,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $match = [regex]::Match(
        $Content,
        '\A---\r?\nname: (?<name>[^\r\n]+)\r?\ndescription: (?<description>[^\r\n]+)\r?\n---\r?\n')
    if (-not $match.Success)
    {
        Add-ValidationError "Skill '$Path' must start with portable name and description frontmatter."
        return $null
    }

    return @{
        Name = $match.Groups['name'].Value.Trim()
        Description = $match.Groups['description'].Value.Trim()
    }
}

if (-not (Test-Path -LiteralPath $toolSourcePath -PathType Leaf))
{
    throw "Cannot find the MCP tool catalog at '$toolSourcePath'."
}

$toolSource = Get-Content -LiteralPath $toolSourcePath -Raw
$toolNames = @(
    [regex]::Matches($toolSource, '\[McpServerTool\(Name = "(?<name>[^"]+)"') |
        ForEach-Object { $_.Groups['name'].Value } |
        Sort-Object -Unique
)

if ($toolNames.Count -eq 0)
{
    throw 'No MCP tools were discovered in RoslynTools.cs.'
}

foreach ($toolName in $toolNames)
{
    $skillName = "csharp-roslyn-$($toolName.Replace('_', '-'))"
    $codexSkillPath = Join-Path $codexSkillsPath "$skillName\SKILL.md"
    $openAiYamlPath = Join-Path $codexSkillsPath "$skillName\agents\openai.yaml"
    $claudeSkillPath = Join-Path $claudeSkillsPath "$skillName\SKILL.md"

    if (-not (Test-Path -LiteralPath $codexSkillPath -PathType Leaf))
    {
        Add-ValidationError "MCP tool '$toolName' has no canonical skill at '$codexSkillPath'."
        continue
    }

    $canonicalContent = Get-Content -LiteralPath $codexSkillPath -Raw
    $canonicalFrontmatter = Get-SkillFrontmatter -Content $canonicalContent -Path $codexSkillPath
    if ($null -ne $canonicalFrontmatter)
    {
        if ($canonicalFrontmatter.Name -ne $skillName)
        {
            Add-ValidationError "Canonical skill '$codexSkillPath' declares '$($canonicalFrontmatter.Name)' instead of '$skillName'."
        }

        if ($canonicalFrontmatter.Description -match 'TODO|\[TODO')
        {
            Add-ValidationError "Canonical skill '$codexSkillPath' still has a placeholder description."
        }

        if ($canonicalFrontmatter.Name.Length -gt 64 -or $canonicalFrontmatter.Name -notmatch '^[a-z0-9-]+$')
        {
            Add-ValidationError "Canonical skill name '$($canonicalFrontmatter.Name)' is not portable Agent Skills metadata."
        }
    }

    if ($canonicalContent -notmatch [regex]::Escape("``$toolName``"))
    {
        Add-ValidationError "Canonical skill '$skillName' does not name the exact MCP tool '$toolName'."
    }

    if ($canonicalContent -match '\[TODO|Structuring This Skill')
    {
        Add-ValidationError "Canonical skill '$skillName' still contains initializer guidance."
    }

    if (-not (Test-Path -LiteralPath $openAiYamlPath -PathType Leaf))
    {
        Add-ValidationError "Canonical skill '$skillName' has no agents/openai.yaml metadata."
    }
    else
    {
        $openAiYaml = Get-Content -LiteralPath $openAiYamlPath -Raw
        if ($openAiYaml -notmatch [regex]::Escape("Use `$$skillName"))
        {
            Add-ValidationError "OpenAI metadata for '$skillName' does not explicitly invoke `$$skillName."
        }

        if ($openAiYaml -notmatch '(?m)^\s+value: "csharp_roslyn"\s*$')
        {
            Add-ValidationError "OpenAI metadata for '$skillName' does not declare the csharp_roslyn MCP dependency."
        }

        $shortDescription = [regex]::Match($openAiYaml, '(?m)^\s+short_description: "(?<value>[^"]+)"\s*$')
        if (-not $shortDescription.Success -or
            $shortDescription.Groups['value'].Value.Length -lt 25 -or
            $shortDescription.Groups['value'].Value.Length -gt 64)
        {
            Add-ValidationError "OpenAI metadata for '$skillName' needs a 25-64 character short_description."
        }
    }

    if (-not (Test-Path -LiteralPath $claudeSkillPath -PathType Leaf))
    {
        Add-ValidationError "MCP tool '$toolName' has no Claude adapter at '$claudeSkillPath'."
        continue
    }

    $claudeContent = Get-Content -LiteralPath $claudeSkillPath -Raw
    $claudeFrontmatter = Get-SkillFrontmatter -Content $claudeContent -Path $claudeSkillPath
    if ($null -ne $claudeFrontmatter -and $claudeFrontmatter.Name -ne $skillName)
    {
        Add-ValidationError "Claude adapter '$claudeSkillPath' declares '$($claudeFrontmatter.Name)' instead of '$skillName'."
    }

    $expectedCanonicalReference = "../../../.agents/skills/$skillName/SKILL.md"
    if ($claudeContent -notmatch [regex]::Escape($expectedCanonicalReference))
    {
        Add-ValidationError "Claude adapter '$skillName' does not reference its canonical skill."
    }

    if ($claudeContent -notmatch [regex]::Escape("``$toolName``"))
    {
        Add-ValidationError "Claude adapter '$skillName' does not name the exact MCP tool '$toolName'."
    }
}

$expectedSkillNames = @($toolNames | ForEach-Object { "csharp-roslyn-$($_.Replace('_', '-'))" })
$unexpectedCanonicalSkills = @(
    Get-ChildItem -LiteralPath $codexSkillsPath -Directory |
        Where-Object { $_.Name -notin $expectedSkillNames } |
        Select-Object -ExpandProperty Name
)
$unexpectedClaudeSkills = @(
    Get-ChildItem -LiteralPath $claudeSkillsPath -Directory |
        Where-Object { $_.Name -notin $expectedSkillNames } |
        Select-Object -ExpandProperty Name
)

foreach ($unexpectedSkill in $unexpectedCanonicalSkills)
{
    Add-ValidationError "Canonical skill '$unexpectedSkill' does not map to an exposed MCP tool."
}

foreach ($unexpectedSkill in $unexpectedClaudeSkills)
{
    Add-ValidationError "Claude skill '$unexpectedSkill' does not map to an exposed MCP tool."
}

if ($errors.Count -gt 0)
{
    foreach ($validationError in $errors)
    {
        [Console]::Error.WriteLine($validationError)
    }

    throw "Roslyn skill validation failed with $($errors.Count) error(s)."
}

Write-Host "Validated $($toolNames.Count) MCP tools, canonical Codex skills, Claude adapters, and OpenAI dependency manifests."
