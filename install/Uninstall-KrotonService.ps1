param(
    [string]$ServiceName = "KrotonIAService",
    [string]$OldTaskName = "KrotonIA-GameModeRunner",
    [switch]$EnableTaskBack,
    [string]$NssmVersion = "2.24"
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    throw "Execute este script em PowerShell como Administrador."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$nssmExe = Join-Path $scriptDir "tools\nssm-$NssmVersion\win64\nssm.exe"

if (Test-Path -Path $nssmExe) {
    try {
        & $nssmExe stop $ServiceName confirm | Out-Null
    }
    catch {
    }

    try {
        & $nssmExe remove $ServiceName confirm | Out-Null
    }
    catch {
    }
}
else {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        if ($svc.Status -eq "Running") {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        }
        sc.exe delete $ServiceName | Out-Null
    }
}

Write-Host "Servico removido (se existia): $ServiceName"

if ($EnableTaskBack) {
    $task = Get-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        Enable-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue | Out-Null
        Write-Host "Tarefa agendada reativada: $OldTaskName"
    }
}
