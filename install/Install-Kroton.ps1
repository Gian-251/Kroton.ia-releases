# ============================================
# Install-Kroton.ps1 - Instalador Kroton.ia
# ============================================
param(
    [string]$InstallPath = ""
)

$ErrorActionPreference = "Stop"

# Requer administrador
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Este instalador requer privilegios de administrador!"
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Layout do repositorio (scripts em install\, arquivos na raiz) x instalacao plana
$sourceDir = $scriptDir
if (-not (Test-Path (Join-Path $sourceDir "KrotonIA.exe"))) {
    $sourceDir = Split-Path -Parent $scriptDir
}

$taskName = "KrotonIA-GameModeRunner"

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    Add-Type -AssemblyName System.Windows.Forms
    $programFiles = [Environment]::GetFolderPath("ProgramFiles")
    $defaultPath = Join-Path $programFiles "KrotonIA"
    $initialPath = if (Test-Path $defaultPath) { $defaultPath } else { $programFiles }
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Selecione a pasta de destino do Kroton.ia. Selecione Program Files para usar o padrao $defaultPath, ou escolha/crie outra pasta."
    $dialog.ShowNewFolderButton = $true
    $dialog.SelectedPath = $initialPath
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host "Instalacao cancelada pelo usuario."
        exit 1
    }

    $selectedPath = [System.IO.Path]::GetFullPath($dialog.SelectedPath).TrimEnd('\')
    if ([string]::Equals(
        $selectedPath,
        [System.IO.Path]::GetFullPath($programFiles).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
        $InstallPath = $defaultPath
    } else {
        $InstallPath = $selectedPath
    }
}

function Unblock-DownloadedFiles {
    param(
        [string]$RootPath
    )

    if (-not (Test-Path $RootPath)) { return }

    $items = @(Get-ChildItem -Path $RootPath -Recurse -File -Force -ErrorAction SilentlyContinue)
    foreach ($item in $items) {
        try {
            Unblock-File -Path $item.FullName -ErrorAction Stop
        }
        catch {
            # Arquivos extraidos de ZIP podem vir com ADS "Zone.Identifier". O aviso
            # de SmartScreen e' do Windows; remover o marcador reduz o falso positivo.
        }
    }
}

Write-Host "=== KROTON.IA INSTALLER ===" -ForegroundColor Green
Write-Host "Origem:  $sourceDir"
Write-Host "Destino: $InstallPath" -ForegroundColor Yellow

Unblock-DownloadedFiles -RootPath $sourceDir
Unblock-DownloadedFiles -RootPath $InstallPath

# 1. Criar pastas de destino (backup = mesma pasta usada pelo runner)
foreach ($dir in @($InstallPath, (Join-Path $InstallPath "runtime"), (Join-Path $InstallPath "backup"))) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}
Write-Host "[OK] Pastas criadas" -ForegroundColor Green

# 2. Copiar arquivos principais
$files = @(
    "Install-Kroton.bat",
    "Install-Kroton.ps1",
    "Install-KrotonService.ps1",
    "Install-KrotonStartup.ps1",
    "KrotonIA.exe",
    "KrotonGameModeRunner.ps1",
    "Optimize-BackgroundServices.ps1",
    "game-mode-config.json",
    "service-profile.json",
    "service-profile.game.json",
    "service-profile.normal.json",
    "service-profile.light.json",
    "Uninstall-Kroton.bat",
    "Uninstall-Kroton.ps1",
    "Uninstall-KrotonService.ps1",
    "Uninstall-KrotonStartup.ps1",
    "README.md"
)

foreach ($file in $files) {
    # Raiz do projeto primeiro; instaladores ficam ao lado do script (install\)
    $from = Join-Path $sourceDir $file
    if (-not (Test-Path $from)) { $from = Join-Path $scriptDir $file }
    if (-not (Test-Path $from)) {
        Write-Error "Arquivo ausente: $file"
        exit 1
    }
    $to = Join-Path $InstallPath $file
    if ($from -ne $to) {
        Copy-Item -Path $from -Destination $InstallPath -Force
    }
}

# Catalogo de servicos usado pela GUI (aba Servicos do Windows)
$catalogName = "List-CatalogServices.ps1"
$catalogFrom = Join-Path $sourceDir "app\$catalogName"
if (-not (Test-Path $catalogFrom)) { $catalogFrom = Join-Path $scriptDir "app\$catalogName" }
if (-not (Test-Path $catalogFrom)) {
    Write-Error "Arquivo ausente: app\$catalogName"
    exit 1
}
$appDest = Join-Path $InstallPath "app"
if (-not (Test-Path $appDest)) { New-Item -ItemType Directory -Path $appDest -Force | Out-Null }
$catalogTo = Join-Path $appDest $catalogName
if ($catalogFrom -ne $catalogTo) {
    Copy-Item -Path $catalogFrom -Destination $catalogTo -Force
}
# Deteccao de jogos usada pela GUI (aba Jogos) - opcional para pacotes antigos sem o script
$detectName = "Detect-Games.ps1"
$detectFrom = Join-Path $sourceDir "app\$detectName"
if (-not (Test-Path $detectFrom)) { $detectFrom = Join-Path $scriptDir "app\$detectName" }
if (Test-Path $detectFrom) {
    $detectTo = Join-Path $appDest $detectName
    if ($detectFrom -ne $detectTo) { Copy-Item -Path $detectFrom -Destination $detectTo -Force }
} else {
    Write-Host "[!] Opcional ausente: app\$detectName" -ForegroundColor Yellow
}
# Guard de memoria da GUI (aba Memoria) e do runner - opcional para pacotes antigos
$memoryName = "Manage-Memory.ps1"
$memoryFrom = Join-Path $sourceDir "app\$memoryName"
if (-not (Test-Path $memoryFrom)) { $memoryFrom = Join-Path $scriptDir "app\$memoryName" }
if (Test-Path $memoryFrom) {
    $memoryTo = Join-Path $appDest $memoryName
    if ($memoryFrom -ne $memoryTo) { Copy-Item -Path $memoryFrom -Destination $memoryTo -Force }
} else {
    Write-Host "[!] Opcional ausente: app\$memoryName" -ForegroundColor Yellow
}
# Telemetria termica (gatilho do runner) - opcional para pacotes antigos
$telemetryName = "Hardware-Telemetry.ps1"
$telemetryFrom = Join-Path $sourceDir "app\$telemetryName"
if (-not (Test-Path $telemetryFrom)) { $telemetryFrom = Join-Path $scriptDir "app\$telemetryName" }
if (Test-Path $telemetryFrom) {
    $telemetryTo = Join-Path $appDest $telemetryName
    if ($telemetryFrom -ne $telemetryTo) { Copy-Item -Path $telemetryFrom -Destination $telemetryTo -Force }
} else {
    Write-Host "[!] Opcional ausente: app\$telemetryName" -ForegroundColor Yellow
}
Write-Host "[OK] Arquivos copiados" -ForegroundColor Green

# 3. Registrar tarefa agendada (inicia com o Windows)
Write-Host "Registrando tarefa agendada..." -ForegroundColor Yellow
$runnerPath = Join-Path $InstallPath "KrotonGameModeRunner.ps1"

$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $existingTask) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -Force
}

$action = New-ScheduledTaskAction -Execute "PowerShell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runnerPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$taskPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)

Register-ScheduledTask -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings `
    -TaskName $taskName -Description "Kroton.ia - Game Mode Auto-Switch" -Force | Out-Null
Write-Host "[OK] Tarefa registrada" -ForegroundColor Green

# 4. Registrar no Registry (Painel de Controle > Programas e Recursos)
Write-Host "Registrando no Registry..." -ForegroundColor Yellow
$regPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA"
New-Item -Path $regPath -Force | Out-Null
$uninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallPath\Uninstall-Kroton.ps1`""
Set-ItemProperty -Path $regPath -Name "DisplayName" -Value "Kroton.ia - Game Mode Optimizer"
Set-ItemProperty -Path $regPath -Name "DisplayVersion" -Value "1.2"
Set-ItemProperty -Path $regPath -Name "InstallLocation" -Value $InstallPath
Set-ItemProperty -Path $regPath -Name "UninstallString" -Value $uninstallString
Set-ItemProperty -Path $regPath -Name "Publisher" -Value "Kroton.ia"
Set-ItemProperty -Path $regPath -Name "DisplayIcon" -Value (Join-Path $InstallPath "KrotonIA.exe")
Write-Host "[OK] Registro criado" -ForegroundColor Green

# 5. Atalho na Area de Trabalho
Write-Host "Criando atalho na Area de Trabalho..." -ForegroundColor Yellow
try {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut(
        (Join-Path $desktop "Kroton.ia.lnk"))
    $shortcut.TargetPath = Join-Path $InstallPath "KrotonIA.exe"
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Save()
    Write-Host "[OK] Atalho criado" -ForegroundColor Green
}
catch {
    Write-Host "[!] Atalho nao criado: $($_.Exception.Message)" -ForegroundColor Yellow
}

# 6. Iniciar o monitor agora
Write-Host "Iniciando monitor de jogos..." -ForegroundColor Yellow
Start-ScheduledTask -TaskName $taskName

Write-Host ""
Write-Host "=== INSTALACAO CONCLUIDA ===" -ForegroundColor Green
Write-Host "[OK] Instalado em: $InstallPath"
Write-Host "[OK] Inicia automaticamente com o Windows"
Write-Host "[OK] Modo jogo e ativado sozinho ao detectar um jogo"
Write-Host ""
Write-Host "Para desinstalar: Painel de Controle > Programas e Recursos > Kroton.ia"
Write-Host "Ou execute Uninstall-Kroton.bat como administrador."
