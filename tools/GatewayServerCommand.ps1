function Get-GatewayServerCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GatewayRoot,
        [ValidateSet("Python", "Native")]
        [string]$Backend = "Python",
        [string]$Executable = "",
        [ValidateRange(1, 65535)]
        [int]$Port = 8765
    )
    if ([string]::IsNullOrWhiteSpace($Executable)) {
        $Executable = if ($Backend -eq "Native") {
            Join-Path $GatewayRoot "native/build/Release/fluidgateway-native.exe"
        } else {
            (Get-Command python -ErrorAction Stop).Source
        }
    }
    $resolved = (Resolve-Path -LiteralPath $Executable -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Gateway executable must be a file."
    }
    [string[]]$arguments = if ($Backend -eq "Native") {
        @("serve-events")
    } else {
        @("-u", "-m", "fluidgateway", "runtime", "serve-events")
    }
    [pscustomobject]@{
        Executable = $resolved
        Arguments = $arguments + @("--host", "127.0.0.1", "--port", "$Port")
        Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
        Backend = $Backend
    }
}
