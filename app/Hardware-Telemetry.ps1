<#
.SYNOPSIS
    Telemetria de hardware do Kroton.ia: temperatura de CPU e GPU em JSON.

.DESCRIPTION
    Fontes NATIVAS (sem bibliotecas externas, PowerShell 5.1):
    - CPU: MSAcpi_ThermalZoneTemperature (root\wmi) - REQUER admin/elevado (a tarefa
      do runner roda SYSTEM Highest; a GUI sem admin recebe cpuC=null e o gatilho
      termico simplesmente nao dispara).
    - GPU: nvidia-smi.exe (vem com o driver NVIDIA, sem admin) com fallback para o
      namespace root\OpenHardwareMonitor (se o app estiver rodando).
    Ambos falham em silencio: Report SEMPRE devolve JSON de uma linha.

.EXAMPLE
    .\Hardware-Telemetry.ps1 -Mode Report
    {"cpuC":30,"cpuSource":"MSAcpi","gpuC":45,"gpuSource":"nvidia-smi","ts":"..."}

    .\Hardware-Telemetry.ps1 -Mode Probe   # diagnostico: lista as fontes testadas
#>
param(
    [ValidateSet('Report', 'Probe')]
    [string]$Mode = 'Report'
)

$ErrorActionPreference = 'SilentlyContinue'

function Get-CpuTempC {
    # Fonte 1: MSAcpi (zona termica ACPI - maior zona = CPU/placa sob carga)
    try {
        $zones = Get-CimInstance -Namespace 'root\wmi' -ClassName 'MSAcpi_ThermalZoneTemperature' -ErrorAction Stop
        $temps = @()
        foreach ($z in $zones) {
            $c = ($z.CurrentTemperature / 10.0) - 273.15
            if ($c -gt 0 -and $c -lt 150) { $temps += $c }
        }
        if ($temps.Count -gt 0) {
            $max = ($temps | Measure-Object -Maximum).Maximum
            return @{ C = [int][math]::Round($max); Source = 'MSAcpi' }
        }
        return @{ C = $null; Source = 'MSAcpi:sem-zona' }
    }
    catch { return @{ C = $null; Source = 'MSAcpi:sem-acesso' } }
}

function Get-GpuTempC {
    # Fonte 1: nvidia-smi (driver NVIDIA)
    try {
        $smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
        if (Test-Path $smi) {
            $out = @(& $smi --query-gpu=temperature.gpu --format=csv,noheader,nounits 2>$null)
            $vals = @()
            foreach ($line in $out) {
                $v = ("$line".Trim()) -as [int]
                if ($null -ne $v -and $v -gt 0 -and $v -lt 150) { $vals += $v }
            }
            if ($vals.Count -gt 0) {
                $max = ($vals | Measure-Object -Maximum).Maximum
                return @{ C = [int]$max; Source = 'nvidia-smi' }
            }
        }
    }
    catch { }
    # Fonte 2: OpenHardwareMonitor (so se o app estiver rodando)
    try {
        $sensors = @(Get-CimInstance -Namespace 'root\OpenHardwareMonitor' -ClassName 'Sensor' -ErrorAction Stop |
            Where-Object { $_.SensorType -eq 'Temperature' -and "$($_.Name)" -match 'GPU|Core' })
        if ($sensors.Count -gt 0) {
            $max = ($sensors | ForEach-Object { [int]$_.Value } | Measure-Object -Maximum).Maximum
            if ($max -gt 0) { return @{ C = [int]$max; Source = 'OpenHardwareMonitor' } }
        }
    }
    catch { }
    return @{ C = $null; Source = 'indisponivel' }
}

if ($Mode -eq 'Probe') {
    # Diagnostico (README: troubleshooting termico)
    Write-Output "cpu: $(Get-CpuTempC | ForEach-Object { "$($_.Source)=$($_.C)" })"
    Write-Output "gpu: $(Get-GpuTempC | ForEach-Object { "$($_.Source)=$($_.C)" })"
    Write-Output "admin: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
    return
}

# Report: JSON de UMA linha - nunca lanca excecao (o runner faz parse cego)
try {
    $cpu = Get-CpuTempC
    $gpu = Get-GpuTempC
    $report = [PSCustomObject]@{
        cpuC      = $cpu.C
        cpuSource = $cpu.Source
        gpuC      = $gpu.C
        gpuSource = $gpu.Source
        ts        = (Get-Date).ToString('s')
    }
    ConvertTo-Json -InputObject $report -Compress
}
catch {
    ConvertTo-Json -InputObject ([PSCustomObject]@{
        cpuC = $null; cpuSource = 'erro'; gpuC = $null; gpuSource = 'erro'
        ts = (Get-Date).ToString('s')
    }) -Compress
}
