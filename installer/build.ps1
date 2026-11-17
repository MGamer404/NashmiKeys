$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NashmiKeys.App'
$dist = Join-Path $root 'dist'
$iss = Join-Path $PSScriptRoot 'NashmiKeys.iss'
$setup = Join-Path $PSScriptRoot 'output\NashmiKeys-1.0-Setup.exe'

Write-Host "Publishing NashmiKeys to $dist ..."
if (Test-Path -LiteralPath $dist) {
    Get-ChildItem -LiteralPath $dist -Force | Remove-Item -Recurse -Force
}
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $dist
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$candidates = @()
if ($env:INNO_SETUP_ISCC) {
    $candidate = $env:INNO_SETUP_ISCC
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $candidate = Join-Path $candidate 'ISCC.exe'
    }
    $candidates += $candidate
}
$candidates += Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'
$candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
if (${env:ProgramFiles(x86)}) {
    $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}

$iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $iscc) {
    throw 'ISCC.exe not found. Set INNO_SETUP_ISCC or install Inno Setup 6.'
}

Write-Host "Compiling $iss ..."
& $iscc $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Expected setup was not produced: $setup"
}

$item = Get-Item -LiteralPath $setup
Write-Host ("Setup: {0} ({1:N2} MB)" -f $item.FullName, ($item.Length / 1MB))
