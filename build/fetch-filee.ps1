# Fetches the Filee conversion engine (MIT, https://github.com/KnifeLemon/Filee) at the version SpaceSift is tested with.
# It goes to a short folder because Filee's source paths are deep and Windows limits paths to 260 characters.
#
#   powershell -File build\fetch-filee.ps1            # into %LOCALAPPDATA%\SpaceSift.build\Filee
#   powershell -File build\fetch-filee.ps1 -Root C:\src\Filee
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'SpaceSift.build\Filee'),
    [string]$Commit = 'fd693b40e587d4816cea0806d2aeb8b6ba53bd64'
)
$ErrorActionPreference = 'Stop'

if (Test-Path (Join-Path $Root '.git')) {
    $have = (git -C $Root rev-parse HEAD).Trim()
    if ($have -eq $Commit) { Write-Host "Filee $($Commit.Substring(0,7)) is already in $Root"; return }
    git -C $Root fetch --depth 1 origin $Commit
    git -C $Root -c advice.detachedHead=false checkout $Commit
}
else {
    New-Item -ItemType Directory -Force $Root | Out-Null
    git -C $Root init -q
    git -C $Root remote add origin https://github.com/KnifeLemon/Filee.git
    git -C $Root -c core.longpaths=true fetch --depth 1 origin $Commit
    git -C $Root -c core.longpaths=true -c advice.detachedHead=false checkout FETCH_HEAD
}
Write-Host "Filee $($Commit.Substring(0,7)) is in $Root"
