param(
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [Parameter(Mandatory = $true)][string]$StagePath,
    [Parameter(Mandatory = $true)][string]$ReleaseVersion,
    [Parameter(Mandatory = $true)][int]$ParentPid
)

$ErrorActionPreference = "Stop"
$logPath = Join-Path $InstallPath "runtime\github-update.log"
$tag = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $env:LOCALAPPDATA ("KrotonIA\update-backups\" + $ReleaseVersion + "-" + $tag)
$managedBackup = Join-Path $backupRoot "managed-files"
$userBackup = Join-Path $backupRoot "user-data"

function Write-UpdateLog {
    param([string]$Message)
    $line = "{0} {1}{2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message, [Environment]::NewLine
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Copy-UserData {
    param([string]$SourcePath, [string]$BackupPath)
    if (-not (Test-Path -LiteralPath $SourcePath)) { return }
    $relative = $SourcePath.Substring($InstallPath.Length).TrimStart('\')
    $destination = Join-Path $BackupPath $relative
    if (Test-Path -LiteralPath $SourcePath -PathType Container) {
        Copy-Item -LiteralPath $SourcePath -Destination $destination -Recurse -Force
    } else {
        $destinationDir = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $SourcePath -Destination $destination -Force
    }
}

function Test-PreservedUserData {
    param([string]$RelativePath)
    $normalized = $RelativePath.Replace('/', '\')
    if ($normalized.StartsWith("runtime\", [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.StartsWith("backup\", [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($normalized -match '^(game-mode-config\.json|service-profile(?:\.[^\\]+)?\.json)$') { return $true }
    return $false
}

try {
    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
        throw "Pasta de instalacao ausente: $InstallPath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $StagePath "KrotonIA.exe") -PathType Leaf)) {
        throw "Pacote sem KrotonIA.exe: $StagePath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $StagePath "install\Apply-KrotonUpdate.ps1") -PathType Leaf)) {
        throw "Pacote sem Apply-KrotonUpdate.ps1."
    }

    try {
        $parent = Get-Process -Id $ParentPid -ErrorAction Stop
        $parent.WaitForExit()
    } catch {
        # O processo GUI pode ja ter terminado antes do helper iniciar.
    }

    New-Item -ItemType Directory -Path $managedBackup -Force | Out-Null
    New-Item -ItemType Directory -Path $userBackup -Force | Out-Null
    foreach ($relative in @(
        "runtime",
        "backup",
        "game-mode-config.json",
        "service-profile.json",
        "service-profile.game.json",
        "service-profile.normal.json",
        "service-profile.light.json"
    )) {
        Copy-UserData -SourcePath (Join-Path $InstallPath $relative) -BackupPath $userBackup
    }

    $stageRoot = (Resolve-Path -LiteralPath $StagePath).Path.TrimEnd('\') + '\'
    $installRoot = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $StagePath -File -Recurse -Force |
        ForEach-Object {
            $relative = $_.FullName.Substring($stageRoot.Length)
            if (-not (Test-PreservedUserData -RelativePath $relative)) {
                [PSCustomObject]@{ Source = $_.FullName; Relative = $relative }
            }
        } | Sort-Object @{ Expression = { if ($_.Relative -ieq "KrotonIA.exe") { 1 } else { 0 } } })

    if ($files.Count -eq 0) { throw "O pacote nao contem arquivos para atualizar." }

    foreach ($file in $files) {
        $destination = [System.IO.Path]::GetFullPath((Join-Path $InstallPath $file.Relative))
        if (-not $destination.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Caminho de destino fora da instalacao: $($file.Relative)"
        }
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $relativeBackup = Join-Path $managedBackup $file.Relative
            $backupDir = Split-Path -Parent $relativeBackup
            if (-not (Test-Path -LiteralPath $backupDir)) {
                New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $destination -Destination $relativeBackup -Force
        }
    }

    foreach ($file in $files) {
        $destination = Join-Path $InstallPath $file.Relative
        $destinationDir = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.Source -Destination $destination -Force
    }

    $registryPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA"
    if (Test-Path -LiteralPath $registryPath) {
        Set-ItemProperty -LiteralPath $registryPath -Name "DisplayVersion" -Value $ReleaseVersion
    }

    Write-UpdateLog ("Atualizacao {0} aplicada. Runtime, backups e configuracoes do usuario foram preservados; copia local em {1}" -f $ReleaseVersion, $userBackup)
    $exePath = Join-Path $InstallPath "KrotonIA.exe"
    Start-Process -FilePath $exePath
}
catch {
    $message = $_.Exception.Message
    try {
        if (Test-Path -LiteralPath $managedBackup) {
            Get-ChildItem -LiteralPath $managedBackup -File -Recurse -Force | ForEach-Object {
                $relative = $_.FullName.Substring($managedBackup.TrimEnd('\').Length).TrimStart('\')
                $destination = Join-Path $InstallPath $relative
                $destinationDir = Split-Path -Parent $destination
                if (-not (Test-Path -LiteralPath $destinationDir)) {
                    New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
                }
                Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
            }
        }
        Write-UpdateLog ("ERRO ao aplicar release {0}: {1}. Arquivos anteriores restaurados quando existiam; backup em {2}" -f $ReleaseVersion, $message, $backupRoot)
    } catch {
        Write-Error ("Falha no rollback do auto-update. Erro original: {0}; erro do rollback: {1}; backup local: {2}" -f $message, $_.Exception.Message, $backupRoot)
        exit 1
    }
    Write-Error ("Auto-update falhou: {0}. Dados do usuario preservados; backup local: {1}" -f $message, $backupRoot)
    exit 1
}
