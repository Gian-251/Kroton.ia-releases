param(
    [string]$TaskName = "KrotonIA-GameModeRunner",
    [string]$RunnerPath = ".\KrotonGameModeRunner.ps1"
)

$ErrorActionPreference = "Stop"

$fullRunnerPath = Resolve-Path $RunnerPath
$runnerDir = Split-Path -Parent $fullRunnerPath.Path
$arg = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$($fullRunnerPath.Path)`""

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arg -WorkingDirectory $runnerDir
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 0)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive

try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force -ErrorAction Stop | Out-Null
    Write-Host "Tarefa registrada: $TaskName"
}
catch {
    throw "Falha ao registrar tarefa '$TaskName': $($_.Exception.Message). Execute este script em PowerShell como Administrador."
}
