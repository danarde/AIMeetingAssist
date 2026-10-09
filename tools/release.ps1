# Builds the installer, the portable zip and the update packages for one version, into
# releases\<Version>. Upload them with gh release create (docs/development.md, Releases).
#
#   .\tools\release.ps1 -Version 1.1.0 -Notes notes.md
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Notes
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$out = Join-Path $root "releases\$Version"
$publish = Join-Path $out 'publish'
$repo = 'https://github.com/danarde/AIMeetingAssist'

if (Test-Path $out) { throw "$out already exists" }

dotnet publish (Join-Path $root 'src\MeetingAssist.App') -c Release -r win-x64 --self-contained true `
    "-p:Version=$Version" -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# The previous release in the folder lets vpk build a delta package. None exists before the first.
vpk download github --repoUrl $repo -o $out
if ($LASTEXITCODE) { Write-Warning 'No previous release downloaded; packing without a delta' }

vpk pack --packId AIMeetingAssist --packVersion $Version --packDir $publish `
    --mainExe meetingassist.exe --packTitle MeetingAssist --packAuthors "Daniel Ard$([char]0xE9)vol" `
    --icon (Join-Path $root 'src\MeetingAssist.App\app.ico') --releaseNotes $Notes -o $out
if ($LASTEXITCODE) { throw 'vpk pack failed' }

Remove-Item $publish -Recurse
Get-ChildItem $out | Format-Table Name, Length
