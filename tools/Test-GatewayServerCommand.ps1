[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GatewayPath
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $GatewayPath).Path
. (Join-Path $PSScriptRoot "GatewayServerCommand.ps1")
$native = (Resolve-Path -LiteralPath (Join-Path $root "native/build/Release/fluidgateway-native.exe")).Path
$python = (Get-Command python -ErrorAction Stop).Source

foreach ($backend in @("Native", "Python")) {
    $command = Get-GatewayServerCommand -GatewayRoot $root -Backend $backend -Port 9876
    $expected = if ($backend -eq "Native") { $native } else { $python }
    $prefix = if ($backend -eq "Native") { "serve-events" } else { "-u -m fluidgateway runtime serve-events" }
    $hash = (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($command.Backend -ne $backend -or $command.Executable -ne $expected -or
        $command.Sha256 -ne $hash -or
        ($command.Arguments -join " ") -ne "$prefix --host 127.0.0.1 --port 9876") {
        throw "Gateway selector lost the $backend executable, hash, or arguments."
    }
}

$default = Get-GatewayServerCommand -GatewayRoot $root
if ($default.Backend -ne "Native" -or $default.Executable -ne $native) {
    throw "The default must select Native explicitly."
}
$override = Get-GatewayServerCommand -GatewayRoot $root -Executable $native
if ($override.Executable -ne $native -or $override.Sha256 -ne $default.Sha256) {
    throw "An executable override must retain file identity verification."
}
$missing = Join-Path $root ([Guid]::NewGuid().ToString("N") + ".exe")
$rejected = $false
try {
    $null = Get-GatewayServerCommand -GatewayRoot $root -Executable $missing
}
catch {
    $rejected = $true
}
if (-not $rejected) { throw "A missing native executable must not fall back to Python." }

foreach ($name in @("Test-FluidLinkIntegration", "Test-GatewayManagedUpdateUpload",
    "Test-GatewayManagedD3D12Copy", "Test-GatewayManagedVulkanCopy")) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot "$name.ps1"), [ref]$tokens, [ref]$errors)
    if ($errors) { throw "PowerShell parse failed: $name" }
    $parameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "GatewayBackend" }
    if ($parameter.DefaultValue.SafeGetValue() -ne "Native") {
        throw "$name must use the promoted Native default."
    }
}
Write-Host "Gateway selection passed: Native default, explicit Python, hashes, override and no fallback."
