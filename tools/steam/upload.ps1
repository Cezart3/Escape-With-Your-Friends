<#
.SYNOPSIS
    Builds nothing and uploads everything: one command from a built player to a Steam beta branch (#87).

.DESCRIPTION
    Fills the placeholders in app_build.vdf and depot_content.vdf from environment variables, writes
    the filled copies next to the build output, and runs steamcmd non-interactively.

    Nothing secret lives in the repo. Set these before running:

        $env:EWYF_APPID   = "480"                     # the real app id once #85 clears
        $env:EWYF_DEPOT   = "481"                     # normally appid + 1
        $env:EWYF_USER    = "your-steam-build-account"
        $env:EWYF_BRANCH  = "beta"                    # "" uploads without setting anything live

    The account needs to have logged in on this machine once, interactively, so Steam Guard has
    remembered it:  steamcmd +login <user>  then the emailed code. After that this script runs with
    no prompt, which is the whole acceptance criterion.

.EXAMPLE
    pwsh tools/steam/upload.ps1 -Content D:\Builds\EWYF-release -Description "0.1.0 first beta"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Content,
    [string] $Description = "dev build",
    [string] $SteamCmd = "steamcmd",
    [string] $Output = "$env:TEMP\ewyf-steam-build"
)

$ErrorActionPreference = "Stop"

function Need([string] $name) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "$name is not set. See the comment at the top of this script." }
    return $value
}

$appId = Need "EWYF_APPID"
$depot = Need "EWYF_DEPOT"
$user  = Need "EWYF_USER"
$branch = [Environment]::GetEnvironmentVariable("EWYF_BRANCH")

if (-not (Test-Path $Content)) { throw "No build at $Content. Run BuildTool first." }
if (-not (Test-Path "$Content\EscapeWithYourFriends.exe")) { throw "$Content has no player in it." }

New-Item -ItemType Directory -Force $Output | Out-Null

$values = @{
    '$EWYF_APPID'   = $appId
    '$EWYF_DEPOT'   = $depot
    '$EWYF_BRANCH'  = $branch
    '$EWYF_CONTENT' = (Resolve-Path $Content).Path
    '$EWYF_OUTPUT'  = (Resolve-Path $Output).Path
    '$EWYF_DESC'    = $Description
}

foreach ($name in @("app_build.vdf", "depot_content.vdf")) {
    $text = Get-Content (Join-Path $PSScriptRoot $name) -Raw
    foreach ($key in $values.Keys) { $text = $text.Replace($key, $values[$key]) }
    Set-Content -Path (Join-Path $Output $name) -Value $text -Encoding utf8
}

Write-Host "Uploading $Content to app $appId depot $depot" -NoNewline
if ($branch) { Write-Host " on branch '$branch'." } else { Write-Host ", not set live." }

# +login with no password: Steam Guard remembers the machine from the one interactive login.
& $SteamCmd +login $user +run_app_build (Join-Path $Output "app_build.vdf") +quit
if ($LASTEXITCODE -ne 0) { throw "steamcmd failed with $LASTEXITCODE. The log is in $Output." }

Write-Host "Uploaded. Promote the branch from the partner site when somebody has actually run it."
