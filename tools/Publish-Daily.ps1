[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$CMake = 'cmake'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('artifacts/daily-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw 'Output directory already exists. Choose a new path; existing packages are never deleted.'
}
$build = Join-Path $root 'artifacts/daily-native'

# The daily package contains only the read-only probe, not ASAN or experimental hooks.
& $CMake -S (Join-Path $root 'native') -B $build -A x64 `
    -DFLUIDRUNTIME_ENABLE_VULKAN=OFF -DFLUIDRUNTIME_ENABLE_ASAN=OFF `
    -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE) { throw 'Native configuration failed.' }
& $CMake --build $build --config Release --target fluidruntime-native-probe
if ($LASTEXITCODE) { throw 'Native build failed.' }
& $CMake --build $build --config Release --target fluidruntime-native-probe -- `
    /m:1 /nr:false /t:Rebuild /p:RunCodeAnalysis=true /p:CodeAnalysisTreatWarningsAsErrors=true
if ($LASTEXITCODE) { throw 'Native analysis failed.' }
dotnet publish (Join-Path $root 'src/FluidRuntime/FluidRuntime.csproj') `
    -c Release -r win-x64 --self-contained true -o $output -warnaserror
if ($LASTEXITCODE) { throw 'Managed publication failed; output is incomplete.' }
Copy-Item -LiteralPath (Join-Path $build 'Release/fluidruntime-native-probe.exe') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs/daily-use.md') -Destination (Join-Path $output 'README.md')

$runtime = Join-Path $output 'fluidruntime.exe'
& $runtime monitor --help
if ($LASTEXITCODE) { throw 'Portable executable smoke test failed.' }
$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE) { throw 'Unable to read source revision.' }
$dirty = [bool](git -C $root status --porcelain)
if ($LASTEXITCODE) { throw 'Unable to read source status.' }
$files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    format = 'fluidruntime-daily-package-v0.1'; source_revision = $revision
    source_dirty = $dirty; self_contained = $true; native_asan = $false
    built_at_utc = [DateTime]::UtcNow.ToString('o'); files = $files
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding UTF8
Write-Host "Portable package: $output"
Write-Host "Start with: & '$runtime' processes"
