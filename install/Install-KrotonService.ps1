param(
    [string]$ServiceName = "KrotonIAService",
    [string]$DisplayName = "Kroton IA Game Mode Service",
    [string]$Description = "Alterna perfil normal/agressivo do Kroton.ia conforme jogo ativo.",
    [string]$RunnerPath = ".\KrotonGameModeRunner.ps1",
    [string]$OldTaskName = "KrotonIA-GameModeRunner",
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
$runner = Resolve-Path -Path $RunnerPath
$toolsDir = Join-Path $scriptDir "tools"
$nssmRoot = Join-Path $toolsDir "nssm-$NssmVersion"
$nssmExe = Join-Path $nssmRoot "win64\nssm.exe"
$zipPath = Join-Path $toolsDir "nssm-$NssmVersion.zip"
$runtimeDir = Join-Path $scriptDir "runtime"

New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

if (-not (Test-Path -Path $nssmExe)) {
    $url = "https://nssm.cc/release/nssm-$NssmVersion.zip"
    Write-Host "Baixando NSSM de: $url"
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

    $extractDir = Join-Path $toolsDir "nssm-extract-$NssmVersion"
    if (Test-Path -Path $extractDir) {
        Remove-Item -Path $extractDir -Recurse -Force
    }

    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    $expandedRoot = Join-Path $extractDir "nssm-$NssmVersion"

    if (-not (Test-Path -Path $expandedRoot)) {
        throw "Nao foi possivel localizar pasta extraida do NSSM."
    }

    if (Test-Path -Path $nssmRoot) {
        Remove-Item -Path $nssmRoot -Recurse -Force
    }

    Move-Item -Path $expandedRoot -Destination $nssmRoot
    Remove-Item -Path $extractDir -Recurse -Force
}

if (-not (Test-Path -Path $nssmExe)) {
    throw "nssm.exe nao encontrado em: $nssmExe"
}

$task = Get-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    Disable-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue | Out-Null
    Write-Host "Tarefa agendada desabilitada: $OldTaskName"
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    try {
        & $nssmExe stop $ServiceName confirm | Out-Null
    }
    catch {
    }
    & $nssmExe remove $ServiceName confirm | Out-Null
    Start-Sleep -Seconds 1
}

$powershellExe = Join-Path $PSHOME "powershell.exe"
$runnerArg = "-NoProfile -ExecutionPolicy Bypass -File `"$($runner.Path)`""

& $nssmExe install $ServiceName $powershellExe $runnerArg | Out-Null
& $nssmExe set $ServiceName DisplayName $DisplayName | Out-Null
& $nssmExe set $ServiceName Description $Description | Out-Null
& $nssmExe set $ServiceName AppDirectory $scriptDir | Out-Null
& $nssmExe set $ServiceName Start SERVICE_AUTO_START | Out-Null
& $nssmExe set $ServiceName AppStdout (Join-Path $runtimeDir "service-stdout.log") | Out-Null
& $nssmExe set $ServiceName AppStderr (Join-Path $runtimeDir "service-stderr.log") | Out-Null
& $nssmExe set $ServiceName AppRotateFiles 1 | Out-Null
& $nssmExe set $ServiceName AppRotateOnline 1 | Out-Null
& $nssmExe set $ServiceName AppRotateSeconds 86400 | Out-Null

& $nssmExe start $ServiceName | Out-Null

Get-Service -Name $ServiceName | Select-Object Name, DisplayName, Status, StartType | Format-List
