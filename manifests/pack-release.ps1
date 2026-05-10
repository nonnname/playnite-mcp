param(
    [string]$ToolboxPath = "Toolbox.exe",
    [string]$Configuration = "Release",
    [string]$OutputDirectory,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Resolve-Path $projectRoot
$projectFile = Join-Path $projectRoot "PlayniteMcpServer.csproj"
$buildOutput = Join-Path $projectRoot "bin\$Configuration"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "dist"
}

Invoke-Native "dotnet" "build" $projectFile "-c" $Configuration

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Invoke-Native $ToolboxPath "pack" $buildOutput $OutputDirectory

if (-not $SkipVerify) {
    Invoke-Native $ToolboxPath "verify" "installer" (Join-Path $PSScriptRoot "installer.yaml")
    Invoke-Native $ToolboxPath "verify" "addon" (Join-Path $PSScriptRoot "addon.yaml")
}
