[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Dgpu', 'Turbo')]
    [string]$Operation,

    [Parameter(Mandatory)]
    [ValidateSet('Query', 'On', 'Off')]
    [string]$State,

    [ValidatePattern('^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$')]
    [string]$SchemeGuid
)

$ErrorActionPreference = 'Stop'

# Identifiers intentionally match the verified PowerProfiles scripts.
$gpuInstanceId = 'PCI\VEN_10DE&DEV_249D&SUBSYS_12FB1462&REV_A1\4&AFF0EE3&0&0008'
$boostModeGuid = 'be337238-0d82-4146-a960-4f3749d470c7'

function Write-Result {
    param(
        [Parameter(Mandatory)][bool]$Success,
        [Parameter(Mandatory)][string]$Message,
        $Value
    )

    [ordered]@{ success = $Success; message = $Message; value = $Value } |
        ConvertTo-Json -Compress -Depth 4
    exit $(if ($Success) { 0 } else { 1 })
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DGpuState {
    $gpu = Get-PnpDevice -InstanceId $gpuInstanceId -ErrorAction Stop
    return [ordered]@{
        instanceId = $gpuInstanceId
        enabled = ($gpu.Problem -ne 'CM_PROB_DISABLED')
        status = [string]$gpu.Status
        problem = [string]$gpu.Problem
    }
}

function Invoke-PowerCfg {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $null = & powercfg.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "powercfg failed: $($Arguments -join ' ')"
    }
}

try {
    if ($Operation -eq 'Dgpu') {
        if ($State -eq 'Query') {
            Write-Result $true 'dGPU state queried.' (Get-DGpuState)
        }
        if (-not (Test-IsAdministrator)) {
            throw 'Administrator privileges are required to change the dGPU state.'
        }

        $gpu = Get-PnpDevice -InstanceId $gpuInstanceId -ErrorAction Stop
        if ($State -eq 'Off' -and $gpu.Problem -ne 'CM_PROB_DISABLED') {
            $null = Disable-PnpDevice -InstanceId $gpuInstanceId -Confirm:$false -ErrorAction Stop
        }
        elseif ($State -eq 'On' -and $gpu.Problem -eq 'CM_PROB_DISABLED') {
            $null = Enable-PnpDevice -InstanceId $gpuInstanceId -Confirm:$false -ErrorAction Stop
        }
        Write-Result $true "dGPU set to $State." (Get-DGpuState)
    }

    if ($State -eq 'Query') {
        Write-Result $true 'Turbo Boost query is not implemented; no setting was changed.' $null
    }
    if (-not $SchemeGuid) {
        throw 'SchemeGuid must be explicitly supplied when changing Turbo Boost.'
    }
    if (-not (Test-IsAdministrator)) {
        throw 'Administrator privileges are required to change Turbo Boost.'
    }

    # Windows Processor performance boost mode: 0 = Disabled, 1 = Enabled.
    $boostValue = if ($State -eq 'On') { '1' } else { '0' }
    Invoke-PowerCfg @('/setacvalueindex', $SchemeGuid, 'SUB_PROCESSOR', $boostModeGuid, $boostValue)
    Invoke-PowerCfg @('/setdcvalueindex', $SchemeGuid, 'SUB_PROCESSOR', $boostModeGuid, $boostValue)
    Write-Result $true "Turbo Boost set to $State." ([ordered]@{
            schemeGuid = $SchemeGuid
            state = $State
        })
}
catch {
    Write-Result $false $_.Exception.Message $null
}
