param(
    [string]$ConfigPath = ".\game-mode-config.json",
    [int]$PollIntervalSeconds = 0,
    [switch]$RunOnce,
    [switch]$WhatIfApply
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-Dir {
    param([string]$Path)

    if (-not (Test-Path -Path $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Resolve-PathSafe {
    param(
        [string]$BaseDir,
        [string]$RelativeOrAbsolute
    )

    if ([System.IO.Path]::IsPathRooted($RelativeOrAbsolute)) {
        return $RelativeOrAbsolute
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BaseDir $RelativeOrAbsolute))
}

function New-GameProcessNameSet {
    param([string[]]$Names)

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        $trimmed = $name.Trim()
        [void]$set.Add($trimmed)

        if ($trimmed.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$set.Add($trimmed.Substring(0, $trimmed.Length - 4))
        }
    }

    return $set
}

function Get-RunningGameName {
    param([System.Collections.Generic.HashSet[string]]$GameNames)

    $runningNames = Get-Process -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty ProcessName -Unique

    foreach ($processName in $runningNames) {
        if ($GameNames.Contains($processName)) {
            return $processName
        }
    }

    return $null
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$optimizeScriptPath = Join-Path $scriptDir "Optimize-BackgroundServices.ps1"
$runtimeDir = Join-Path $scriptDir "runtime"
$statePath = Join-Path $runtimeDir "game-mode-state.json"
$logPath = Join-Path $runtimeDir "game-mode.log"

Ensure-Dir -Path $runtimeDir

$resolvedConfigPath = Resolve-PathSafe -BaseDir $scriptDir -RelativeOrAbsolute $ConfigPath
if (-not (Test-Path -Path $resolvedConfigPath)) {
    throw "Config nao encontrada: $resolvedConfigPath"
}

$configRaw = Get-Content -Path $resolvedConfigPath -Raw
$config = $configRaw | ConvertFrom-Json
$gameNameSet = New-GameProcessNameSet -Names $config.gameProcessNames

$gameProfile = Resolve-PathSafe -BaseDir $scriptDir -RelativeOrAbsolute $config.gameProfilePath
$normalProfile = Resolve-PathSafe -BaseDir $scriptDir -RelativeOrAbsolute $config.normalProfilePath

# Perfil do Modo Leve (apenas telemetria). Opcional: se nao existir, o modo
# leve cai no perfil game completo (comportamento de seguranca).
$lightProfile = $null
if ($config.lightProfilePath) {
    $lightCandidate = Resolve-PathSafe -BaseDir $scriptDir -RelativeOrAbsolute $config.lightProfilePath
    if (Test-Path -Path $lightCandidate) { $lightProfile = $lightCandidate }
}

if (-not (Test-Path -Path $optimizeScriptPath)) { throw "Script principal nao encontrado: $optimizeScriptPath" }
if (-not (Test-Path -Path $gameProfile)) { throw "Perfil game nao encontrado: $gameProfile" }
if (-not (Test-Path -Path $normalProfile)) { throw "Perfil normal nao encontrado: $normalProfile" }

$interval = if ($PollIntervalSeconds -gt 0) { $PollIntervalSeconds } else { [int]$config.pollIntervalSeconds }
if ($interval -lt 5) { $interval = 5 }

$currentMode = "normal"
if (Test-Path -Path $statePath) {
    try {
        $state = Get-Content -Path $statePath -Raw | ConvertFrom-Json
        if ($state.mode -in @("normal", "game")) {
            $currentMode = [string]$state.mode
        }
    }
    catch {
        $currentMode = "normal"
    }
}

$admin = Test-IsAdmin
if (-not $admin -and -not $WhatIfApply) {
    $msg = "$(Get-Date -Format s) [WARN] Runner sem admin. Use task com RunLevel Highest para aplicar mudancas reais."
    Add-Content -Path $logPath -Value $msg
}

$cycleIndex = 0
$thermalCycle = 0
# Ultimo jogo detectado durante o modo jogo (aviso de troca de jogo ativo)
$script:lastGameName = $null

while ($true) {
    # Recarrega config a cada ciclo para refletir mudancas feitas pelo app GUI
    try {
        $cfgLatest = Get-Content -Path $resolvedConfigPath -Raw | ConvertFrom-Json
        $gameNameSet = New-GameProcessNameSet -Names $cfgLatest.gameProcessNames
        $activeMode = if ($cfgLatest.activeMode) { [string]$cfgLatest.activeMode } else { "game" }
    }
    catch {
        $activeMode = "game"
    }

    if ($activeMode -eq "disabled") {
        # Otimizacao desativada pelo usuario: se estava em modo otimizado, restaura para normal
        if ($currentMode -ne "normal") {
            $gameBackupPath = Join-Path $scriptDir (Join-Path "backup" "services-backup-game.json")
            if (Test-Path -Path $gameBackupPath) {
                try {
                    & $optimizeScriptPath -Mode Rollback -BackupPath $gameBackupPath
                    $currentMode = "normal"
                    [PSCustomObject]@{ mode = "normal"; changedAt = (Get-Date).ToString("s") } |
                        ConvertTo-Json | Set-Content -Path $statePath -Encoding UTF8
                } catch { }
            }
        }
        if ($RunOnce) { break }
        Start-Sleep -Seconds $interval
        continue
    }

    $gameName = Get-RunningGameName -GameNames $gameNameSet
    $targetMode = if ($null -ne $gameName) { "game" } else { "normal" }

    # Jogo ativo mudou durante o modo jogo (ex.: fechou A e abriu B): o perfil
    # personalizado NAO e' reaplicado sozinho - fechar e reabrir o jogo troca
    # de perfil na proxima transicao. Apenas registra no log.
    if ($targetMode -eq "game" -and $currentMode -eq "game" -and
        $null -ne $script:lastGameName -and
        -not [string]::Equals($script:lastGameName, $gameName, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Content -Path $logPath -Value ("$(Get-Date -Format s) [INFO] Jogo ativo mudou: $($script:lastGameName) -> $gameName (perfil nao reaplicado; feche e reabra o jogo para trocar de perfil)")
    }
    $script:lastGameName = $gameName

    if ($targetMode -ne $currentMode) {
        # Entrando no modo jogo: aplica o perfil game e grava services-backup-game.json
        # com o estado ATUAL (o original) antes de mexer em qualquer servico.
        # Saindo: restaura esse backup via Rollback - unica forma exata de devolver
        # StartMode + Running de todos os servicos ao estado anterior ao modo jogo.
        $gameBackupPath = Join-Path $scriptDir (Join-Path "backup" "services-backup-game.json")
        $normalBackupPath = Join-Path $scriptDir (Join-Path "backup" "services-backup-normal.json")

        $changeMsg = "$(Get-Date -Format s) [MODE] $currentMode -> $targetMode"
        Write-Host $changeMsg
        Add-Content -Path $logPath -Value $changeMsg

        if ($targetMode -eq "game") {
            # Modo Leve (activeMode=light): aplica somente o perfil de telemetria;
            # o backup continua o mesmo, entao o Rollback ao sair e identico.
            $profileToApply = $gameProfile
            if ($activeMode -eq "light" -and $lightProfile) {
                $profileToApply = $lightProfile
            }

            # Perfil personalizado por jogo (perGameProfiles no config): quando o
            # jogo detectado tem profilePath proprio, ele prevalece sobre o perfil
            # base acima. O backup continua o mesmo - o Rollback nao muda.
            $customNote = ""
            if ($null -ne $gameName -and $null -ne $cfgLatest -and
                $cfgLatest.PSObject.Properties['perGameProfiles']) {
                $perGameCfg = $cfgLatest.PSObject.Properties['perGameProfiles'].Value
                if ($null -ne $perGameCfg) {
                    foreach ($entryProp in $perGameCfg.PSObject.Properties) {
                        if (-not [string]::Equals($entryProp.Name, $gameName,
                                [System.StringComparison]::OrdinalIgnoreCase)) {
                            continue
                        }
                        $entryValue = $entryProp.Value
                        if ($null -ne $entryValue -and $entryValue.PSObject.Properties['profilePath']) {
                            $entryProfile = [string]$entryValue.PSObject.Properties['profilePath'].Value
                            if (-not [string]::IsNullOrWhiteSpace($entryProfile)) {
                                $entryProfileResolved = Resolve-PathSafe -BaseDir $scriptDir -RelativeOrAbsolute $entryProfile
                                if (Test-Path -Path $entryProfileResolved) {
                                    $profileToApply = $entryProfileResolved
                                    $customNote = " [perfil personalizado do jogo $gameName]"
                                    Add-Content -Path $logPath -Value ("$(Get-Date -Format s) [INFO] Perfil personalizado: $entryProfileResolved (jogo: $gameName)")
                                }
                            }
                        }
                        break
                    }
                }
            }

            $params = @{
                Mode = "Apply"
                ProfilePath = $profileToApply
                BackupPath = $gameBackupPath
            }
        }
        elseif (Test-Path -Path $gameBackupPath) {
            $params = @{
                Mode = "Rollback"
                BackupPath = $gameBackupPath
            }
        }
        else {
            # Fallback: sem backup disponivel, aplica perfil normal
            $params = @{
                Mode = "Apply"
                ProfilePath = $normalProfile
                BackupPath = $normalBackupPath
            }
        }
        if ($WhatIfApply) { $params.WhatIf = $true }

        try {
            # Captura todas as streams (*>&1): o Optimize imprime [SUMMARY] via
            # Write-Host (stream Information no PS5.1). Reimprime no console para
            # manter a saida da tarefa agendada legivel no Historico de Logs.
            $applyLines = @(& $optimizeScriptPath @params *>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.InformationRecord]) {
                    $_.MessageData.ToString()
                }
                else {
                    $_.ToString()
                }
            })
            $applyOut = [string]::Join([Environment]::NewLine, $applyLines)
            if ($applyOut.Length -gt 0) { Write-Host $applyOut }

            # Resumo do Optimize: quantos servicos realmente parados neste Apply
            # e QUAIS (names=A|B|C) - exposto na aba "Modo Jogo" da GUI.
            $stopped = 0
            $stoppedNames = @()
            if ($applyOut -match '\[SUMMARY\]\s+stopped=(\d+)(?:;names=([^\r\n]*))?') {
                $stopped = [int]$Matches[1]
                if ($Matches[2]) {
                    $stoppedNames = @($Matches[2] -split '\|' | Where-Object { $_ })
                }
            }
            # WhatIf e simulacao: nao afirma quais servicos teriam parado.
            if ($WhatIfApply) { $stoppedNames = @() }

            $verb = if ($params.Mode -eq "Rollback") { "Backup restaurado" } else { "Perfil aplicado" }
            $detail = ""
            if ($targetMode -eq "game" -and $params.Mode -eq "Apply") {
                # WhatIfApply e' simulacao: nao afirma que parou servico de verdade.
                $detail = if ($WhatIfApply) { " (WhatIf: simulacao)" } else { " ($stopped servico(s) parado(s) para o jogo)" + $customNote }
            }
            $ok = "$(Get-Date -Format s) [OK] $verb -> $targetMode$detail"
            Add-Content -Path $logPath -Value $ok
            $currentMode = $targetMode
            [PSCustomObject]@{
                mode                 = $currentMode
                changedAt            = (Get-Date).ToString("s")
                servicesStopped      = $stopped
                servicesStoppedNames = @($stoppedNames)
            } | ConvertTo-Json | Set-Content -Path $statePath -Encoding UTF8
        }
        catch {
            $err = "$(Get-Date -Format s) [ERR] Falha ao aplicar perfil ${targetMode}: $($_.Exception.Message)"
            Write-Warning $err
            Add-Content -Path $logPath -Value $err
        }
    }

    # Guard de memoria SOMENTE no modo jogo (a cada N ciclos do config).
    # Libera RAM de processos nao prioritarios: jogo, Discord/Spotify, processos
    # com audio ativo, processo principal dos navegadores e criticos do Windows
    # nunca sao tocados. Log proprio em runtime\memory-guard.log.
    if ($targetMode -eq "game") {
        $memoryGuard = $null
        try {
            if ($null -ne $cfgLatest -and $cfgLatest.PSObject.Properties['memoryGuard']) {
                $memoryGuard = $cfgLatest.PSObject.Properties['memoryGuard'].Value
            }
        }
        catch { $memoryGuard = $null }

        $guardEnabled = $false
        $guardEvery = 3
        if ($null -ne $memoryGuard) {
            try {
                if ($memoryGuard.PSObject.Properties['enabled']) {
                    $guardEnabled = [bool]$memoryGuard.PSObject.Properties['enabled'].Value
                }
                if ($memoryGuard.PSObject.Properties['everyCycles']) {
                    $guardEvery = [int]$memoryGuard.PSObject.Properties['everyCycles'].Value
                }
            }
            catch { $guardEnabled = $false }
            if ($guardEvery -lt 1) { $guardEvery = 1 }
        }

        if ($guardEnabled) {
            $cycleIndex++
            if (($cycleIndex % $guardEvery) -eq 0) {
                $memoryScriptPath = Join-Path $scriptDir (Join-Path "app" "Manage-Memory.ps1")
                if (Test-Path -Path $memoryScriptPath) {
                    try {
                        & $memoryScriptPath -Mode Guard
                    }
                    catch {
                        Add-Content -Path $logPath -Value ("$(Get-Date -Format s) [WARN] Guard de memoria: " + $_.Exception.Message)
                    }
                }
            }
        }
    }

    # Gatilho termico (telemetria de hardware) SOMENTE no modo jogo: le CPU/GPU via
    # app\Hardware-Telemetry.ps1 (MSAcpi exige admin = tarefa SYSTEM Highest; GPU via
    # nvidia-smi) e, quando a CPU >= thermalGuard.maxCpuTempC, loga [TEMP] no
    # game-mode.log e FORCA a limpeza de memoria (mesma acao do botao -Force).
    if ($targetMode -eq "game") {
        $thermalGuard = $null
        try {
            if ($null -ne $cfgLatest -and $cfgLatest.PSObject.Properties['thermalGuard']) {
                $thermalGuard = $cfgLatest.PSObject.Properties['thermalGuard'].Value
            }
        }
        catch { $thermalGuard = $null }

        $thermalEnabled = $false
        $thermalMax = 85
        $thermalEvery = 1
        if ($null -ne $thermalGuard) {
            try {
                if ($thermalGuard.PSObject.Properties['enabled']) {
                    $thermalEnabled = [bool]$thermalGuard.PSObject.Properties['enabled'].Value
                }
                if ($thermalGuard.PSObject.Properties['maxCpuTempC']) {
                    $thermalMax = [int]$thermalGuard.PSObject.Properties['maxCpuTempC'].Value
                }
                if ($thermalGuard.PSObject.Properties['everyCycles']) {
                    $thermalEvery = [int]$thermalGuard.PSObject.Properties['everyCycles'].Value
                }
            }
            catch { $thermalEnabled = $false }
            if ($thermalEvery -lt 1) { $thermalEvery = 1 }
        }

        if ($thermalEnabled) {
            $thermalCycle++
            if (($thermalCycle % $thermalEvery) -eq 0) {
                $telemetryScriptPath = Join-Path $scriptDir (Join-Path "app" "Hardware-Telemetry.ps1")
                if (Test-Path -Path $telemetryScriptPath) {
                    try {
                        $tOut = @(& $telemetryScriptPath -Mode Report)
                        $t = ($tOut -join "`n") | ConvertFrom-Json
                        if ($null -ne $t.cpuC -and [int]$t.cpuC -ge $thermalMax) {
                            $gpuTxt = if ($null -ne $t.gpuC) { "$($t.gpuC)C" } else { "n/d" }
                            Add-Content -Path $logPath -Value (
                                "$(Get-Date -Format s) [TEMP] cpu=$($t.cpuC)C >= ${thermalMax}C (gpu=$gpuTxt) " +
                                "-> limpeza forcada (gatilho termico)")
                            $memoryScriptPath = Join-Path $scriptDir (Join-Path "app" "Manage-Memory.ps1")
                            if (Test-Path -Path $memoryScriptPath) {
                                try { & $memoryScriptPath -Mode Guard -Force }
                                catch {
                                    Add-Content -Path $logPath -Value ("$(Get-Date -Format s) [WARN] Guard termico: " + $_.Exception.Message)
                                }
                            }
                        }
                    }
                    catch {
                        Add-Content -Path $logPath -Value ("$(Get-Date -Format s) [WARN] Telemetria termica: " + $_.Exception.Message)
                    }
                }
            }
        }
    }

    if ($RunOnce) { break }

    Start-Sleep -Seconds $interval
}
