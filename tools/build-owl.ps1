# Build owl.exe (windows/owl): OWL at the commit in `pins`, with patches/,
# for an ESP32 radio and a Wintun awdl0. Needs git, CMake, Ninja and MinGW GCC
# on PATH; touches no radio and needs no admin.
#   tools/build-owl.ps1            -> windows/owl/build/owl.exe (+ wintun.dll)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Up = Join-Path $Root 'upstream'
$Owl = Join-Path $Up 'owl'
$OwlRepo = 'https://github.com/jedbillyb/owl.git'
$WintunUrl = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
$WintunSha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'

$pins = @{}
Get-Content (Join-Path $Root 'pins') | Where-Object { $_ -match '^\s*([A-Z_]+)=(\S+)' } |
    ForEach-Object { $pins[$Matches[1]] = $Matches[2] }
$commit = $pins['OWL_COMMIT']

function git_ { git -C $Owl @args; if ($LASTEXITCODE) { throw "git $args failed" } }

New-Item -ItemType Directory -Force $Up | Out-Null
if (-not (Test-Path (Join-Path $Owl '.git'))) {
    git clone -q --recurse-submodules $OwlRepo $Owl
    if ($LASTEXITCODE) { throw 'clone failed' }
}
# The tree is reset on every build: edits in upstream/owl belong in a patch.
git -C $Owl fetch -q origin 2>$null
git_ checkout -q -- .
git_ -c advice.detachedHead=false checkout -q $commit
git_ submodule update -q --init --recursive
Get-ChildItem (Join-Path $Root 'patches/owl-*.patch') | Sort-Object Name | ForEach-Object {
    git -C $Owl apply $_.FullName
    if ($LASTEXITCODE) { throw "$($_.Name) did not apply to owl@$commit" }
}

$Wintun = Join-Path $Up 'wintun'
if (-not (Test-Path (Join-Path $Wintun 'include/wintun.h'))) {
    $dl = Join-Path $Up 'wintun-dl'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $zip = Join-Path $dl 'wintun.zip'
    Invoke-WebRequest $WintunUrl -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $WintunSha256) { throw 'wintun.zip checksum mismatch' }
    Expand-Archive $zip -DestinationPath $dl -Force
    Move-Item (Join-Path $dl 'wintun') $Wintun
}

$src = Join-Path $Root 'windows/owl'
$build = Join-Path $src 'build'
cmake -S $src -B $build -G Ninja -DCMAKE_C_COMPILER=gcc -DCMAKE_BUILD_TYPE=RelWithDebInfo | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build $build
if ($LASTEXITCODE) { throw 'build failed' }
Write-Host "built $build\owl.exe: owl@$($commit.Substring(0,7)) + $((Get-ChildItem (Join-Path $Root 'patches/owl-*.patch')).Count) patches"
