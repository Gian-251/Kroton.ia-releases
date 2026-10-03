# ============================================
# Uninstall-Kroton.ps1 - Desinstalador Kroton.ia
# ============================================
param(
    [string]$InstallPath = ""
)

$ErrorActionPreference = "Continue"

# Requer administrador
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Este desinstalador requer privilegios de administrador!"
    exit 1
}

Write-Host "=== KROTON.IA UNINSTALLER ===" -ForegroundColor Yellow

# Caminho de instalacao: parametro > Registry > pasta deste script
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$removeDir = $false
if (-not [string]::IsNullOrWhiteSpace($InstallPath)) {
    # Caminho explicito pelo chamador: confiar e permitir remocao
    $removeDir = $true
}
else {
    $reg = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" -ErrorAction SilentlyContinue
    if ($null -ne $reg -and $reg.InstallLocation) {
        $InstallPath = [string]$reg.InstallLocation
        $removeDir = $true
    }
    else {
        # Layout do repositorio/pacote: scripts em install\, exe na pasta pai.
        # So remove a pasta se for o layout instalado (exe ao lado do script);
        # nunca remove a pasta raiz do pacote/repositorio.
        $InstallPath = $scriptDir
        if (-not (Test-Path (Join-Path $InstallPath "KrotonIA.exe"))) {
            $InstallPath = Split-Path -Parent $scriptDir
        }
        $removeDir = Test-Path (Join-Path $scriptDir "KrotonIA.exe")
    }
}
Write-Host "Instalacao: $InstallPath"

# 1. Parar e remover o servico Windows (se existir - usa NSSM)
$serviceScript = Join-Path $InstallPath "Uninstall-KrotonService.ps1"
if (Test-Path $serviceScript) {
    Write-Host "Removendo servico Windows (se existir)..."
    try { & $serviceScript | Out-Null }
    catch { Write-Host "  (nenhum servico para remover)" }
}

# 2. Parar e remover a tarefa agendada
Write-Host "Removendo tarefa agendada..."
$taskScript = Join-Path $InstallPath "Uninstall-KrotonStartup.ps1"
if (Test-Path $taskScript) {
    try { & $taskScript | Out-Null } catch { }
}
else {
    Stop-ScheduledTask -TaskName "KrotonIA-GameModeRunner" -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName "KrotonIA-GameModeRunner" -Confirm:$false -ErrorAction SilentlyContinue
}
Write-Host "[OK] Tarefa removida" -ForegroundColor Green

# 3. Restaurar servicos aos valores originais.
#    services-backup-game.json guarda o estado ANTES do modo jogo (o estado original).
$optimizeScript = Join-Path $InstallPath "Optimize-BackgroundServices.ps1"
$gameBackup = Join-Path $InstallPath "backup\services-backup-game.json"
$normalProfile = Join-Path $InstallPath "service-profile.normal.json"
$restored = $false

Write-Host "Restaurando servicos do Windows..."
if ((Test-Path $optimizeScript) -and (Test-Path $gameBackup)) {
    try {
        & $optimizeScript -Mode Rollback -BackupPath $gameBackup
        $restored = $true
    }
    catch {
        Write-Host "[!] Rollback falhou: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if (-not $restored -and (Test-Path $optimizeScript) -and (Test-Path $normalProfile)) {
    Write-Host "  Backup nao encontrado. Aplicando perfil normal como fallback..."
    try {
        $fallbackBackup = Join-Path $InstallPath "backup\services-backup-fallback.json"
        & $optimizeScript -Mode Apply -ProfilePath $normalProfile -BackupPath $fallbackBackup
        $restored = $true
    }
    catch {
        Write-Host "[!] Falha ao aplicar perfil normal: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if ($restored) {
    Write-Host "[OK] Servicos restaurados" -ForegroundColor Green
}
else {
    Write-Host "[!] Servicos nao foram restaurados. Verifique $InstallPath" -ForegroundColor Yellow
}

# 4. Remover atalho da Area de Trabalho
$desktop = [Environment]::GetFolderPath("Desktop")
$lnkPath = Join-Path $desktop "Kroton.ia.lnk"
if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Atalho removido" -ForegroundColor Green
}

# 5. Remover entrada do Registry (Painel de Controle)
Write-Host "Removendo registro do Painel de Controle..."
Remove-Item "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[OK] Registro removido" -ForegroundColor Green

# 6. Remover arquivos instalados
if ($removeDir) {
    Write-Host "Removendo arquivos..."
    if (Test-Path $InstallPath) {
        try {
            Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction Stop
            Write-Host "[OK] Arquivos removidos" -ForegroundColor Green
        }
        catch {
            Write-Host "[!] Nao foi possivel remover tudo: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "    Feche janelas abertas em $InstallPath e remova a pasta manualmente."
        }
    }
}
else {
    Write-Host "[!] Pasta do repositorio/pacote preservada: $InstallPath" -ForegroundColor Yellow
    Write-Host "    Servicos restaurados e tarefa removida. Para apagar os arquivos, delete a pasta manualmente."
}

Write-Host ""
Write-Host "=== DESINSTALACAO CONCLUIDA ===" -ForegroundColor Green
