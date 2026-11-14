# Standalone pure math must remain consumable by D3D11 SM5 and D3D12 SM6 projects.
$ErrorActionPreference = 'Stop'
$shaderDir = Join-Path $PSScriptRoot '../native/bale-gpu/shaders'
$bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$dxc = Get-ChildItem "$bin/*/x64/dxc.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $dxc) { throw 'Windows SDK dxc.exe is required for the standalone SM6 tone-map check' }
if (-not $env:FXC) { throw 'FXC is required for the standalone SM5 tone-map check' }
$out = Join-Path $env:RUNNER_TEMP 'bale-tonemap-math-check'
New-Item -ItemType Directory -Force $out | Out-Null
try {
    $source = Join-Path $PSScriptRoot '../native/bale-gpu/tests/shaders/tonemap_math.hlsl'
    & $env:FXC /nologo /WX /T ps_5_0 /E main /I $shaderDir /Fo "$out/math-sm5.cso" $source
    if ($LASTEXITCODE -ne 0) { throw 'Standalone SM5 tone-map math failed' }
    & $dxc.FullName -WX -T ps_6_0 -E main -I $shaderDir -Fo "$out/math-sm6.cso" $source
    if ($LASTEXITCODE -ne 0) { throw 'Standalone SM6 tone-map math failed' }
} finally { Remove-Item $out -Recurse -Force }
