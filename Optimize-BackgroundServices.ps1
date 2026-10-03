param(
    [ValidateSet("Audit", "Apply", "Rollback")]
    [string]$Mode = "Audit",

    [string]$ProfilePath = ".\service-profile.json",

    [string]$BackupPath = ".\backup\services-backup.json",

    [switch]$IncludeProcessSnapshot,

    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-FolderFromFilePath {
    param([string]$FilePath)

    $folder = Split-Path -Path $FilePath -Parent
    if (-not [string]::IsNullOrWhiteSpace($folder) -and -not (Test-Path -Path $folder)) {
        New-Item -ItemType Directory -Path $folder | Out-Null
    }
}

function Convert-StartModeToStartupType {
    param([string]$StartMode)

    switch ($StartMode) {
        "Auto" { return "Automatic" }
        "Manual" { return "Manual" }
        "Disabled" { return "Disabled" }
        default { return "Manual" }
    }
}

function Get-ServiceSnapshot {
    $cim = Get-CimInstance Win32_Service | Select-Object Name, DisplayName, StartMode, State, PathName
    $byName = @{}
    foreach ($item in $cim) {
        $byName[$item.Name] = $item
    }

    $services = Get-Service | Select-Object Name, DisplayName, Status
    $result = foreach ($svc in $services) {
        $c = $byName[$svc.Name]
        [PSCustomObject]@{
            Name        = $svc.Name
            DisplayName = $svc.DisplayName
            Status      = [string]$svc.Status
            StartMode   = if ($null -ne $c) { [string]$c.StartMode } else { "Unknown" }
            State       = if ($null -ne $c) { [string]$c.State } else { "Unknown" }
            PathName    = if ($null -ne $c) { [string]$c.PathName } else { "" }
        }
    }

    return $result
}

function Matches-AnyPrefix {
    param(
        [string]$Value,
        [string[]]$Prefixes
    )

    foreach ($prefix in $Prefixes) {
        if ([string]::IsNullOrWhiteSpace($prefix)) { continue }
        if ($Value.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Matches-AnyContains {
    param(
        [string]$Value,
        [string[]]$Needles
    )

    foreach ($needle in $Needles) {
        if ([string]::IsNullOrWhiteSpace($needle)) { continue }
        if ($Value.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Resolve-ServiceForStartupChange {
    param([string]$Name)

    # Per-user services (ex: CDPUserSvc_ab12c) geralmente nao aceitam Set-Service direto.
    # Quando existir o servico-base (template), aplicamos StartupType nele.
    if ($Name -match "^(?<base>.+)_[0-9a-fA-F]{4,}$") {
        $baseName = $Matches.base
        $baseSvc = Get-Service -Name $baseName -ErrorAction SilentlyContinue
        if ($null -ne $baseSvc) {
            return $baseName
        }
    }

    return $Name
}

function Build-Recommendations {
    param(
        [object[]]$Snapshot,
        [object]$Profile
    )

    $recs = New-Object System.Collections.Generic.List[object]
    $scanAllRunning = [bool]$Profile.scanAllRunningServices

    foreach ($svc in $Snapshot) {
        $name = [string]$svc.Name
        $display = [string]$svc.DisplayName
        $pathName = [string]$svc.PathName

        $isCritical = Matches-AnyPrefix -Value $name -Prefixes $Profile.criticalNamePrefixes
        if (-not $isCritical) {
            $isCritical = Matches-AnyContains -Value $display -Needles $Profile.criticalDisplayContains
        }

        if ($isCritical) {
            continue
        }

        $isKeepRunning = Matches-AnyPrefix -Value $name -Prefixes $Profile.keepRunningNamePrefixes
        if (-not $isKeepRunning) {
            $isKeepRunning = Matches-AnyContains -Value $display -Needles $Profile.keepRunningDisplayContains
        }

        if ($isKeepRunning) {
            continue
        }

        $matchesExplicitCandidate = Matches-AnyPrefix -Value $name -Prefixes $Profile.candidateNamePrefixes
        if (-not $matchesExplicitCandidate) {
            $matchesExplicitCandidate = Matches-AnyContains -Value $display -Needles $Profile.candidateDisplayContains
        }

        $isCandidate = $false
        if ($scanAllRunning) {
            $isWindowsServicePath = $false
            if (-not [string]::IsNullOrWhiteSpace($pathName)) {
                $isWindowsServicePath = $pathName.IndexOf("\Windows\", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
            }

            # Trava de seguranca: no modo amplo, so pega servicos ativos fora de Windows
            # ou os explicitamente marcados como candidatos no perfil.
            $isCandidate = ($svc.Status -eq "Running") -and ((-not $isWindowsServicePath) -or $matchesExplicitCandidate)
        }
        else {
            $isCandidate = $matchesExplicitCandidate
        }

        if (-not $isCandidate) {
            continue
        }

        $targetStartup = [string]$Profile.defaultTargetStartupType
        if ($name -in $Profile.forceDisableNames) {
            $targetStartup = "Disabled"
        }

        # Ignora servicos que ja estao exatamente no estado desejado.
        # No perfil normal o alvo e Automatic: um servico Manual/Stopped que o modo
        # jogo parou DEVE virar recomendacao, senao ele nunca e restaurado.
        $effectiveStart = switch ($svc.StartMode) {
            "Auto" { "Automatic" }
            "Manual" { "Manual" }
            "Disabled" { "Disabled" }
            default { "Unknown" }
        }

        $alreadyOptimized = $false
        if ($effectiveStart -eq $targetStartup) {
            if ($targetStartup -eq "Automatic") {
                # Alvo Automatic: so esta otimizado se ja estiver rodando
                $alreadyOptimized = ($svc.Status -eq "Running")
            }
            else {
                # Alvo Manual/Disabled: otimizado se estiver parado
                $alreadyOptimized = ($svc.Status -ne "Running")
            }
        }

        if ($alreadyOptimized) {
            continue
        }

        $confidence = 60
        if ($name -in $Profile.forceDisableNames) { $confidence = 85 }
        if ($svc.StartMode -eq "Auto") { $confidence += 10 }
        if ($svc.Status -eq "Running") { $confidence += 5 }
        if ($scanAllRunning) { $confidence -= 5 }

        $reason = if ($scanAllRunning) {
            "Modo scanAllRunningServices: servico ativo nao critico"
        }
        else {
            "Regra de perfil: servico opcional para ambiente gamer/workstation"
        }

        $recs.Add([PSCustomObject]@{
            Name           = $name
            DisplayName    = $display
            CurrentStatus  = [string]$svc.Status
            CurrentStart   = [string]$svc.StartMode
            ProposedStart  = $targetStartup
            ActionStop     = ($svc.Status -eq "Running")
            Confidence     = [Math]::Min([Math]::Max($confidence, 10), 95)
            Reason         = $reason
        })
    }

    return $recs | Sort-Object -Property @{ Expression = "Confidence"; Descending = $true }, @{ Expression = "Name"; Descending = $false }
}

function Save-Backup {
    param(
        [object[]]$Snapshot,
        [string]$Path
    )

    Ensure-FolderFromFilePath -FilePath $Path
    $payload = [PSCustomObject]@{
        CreatedAt = (Get-Date).ToString("s")
        Computer  = $env:COMPUTERNAME
        Services  = $Snapshot
    }

    $payload | ConvertTo-Json -Depth 5 | Set-Content -Path $Path -Encoding UTF8
}

function Apply-Recommendations {
    param(
        [object[]]$Recommendations,
        [switch]$Simulate
    )

    # Contador exposto via [SUMMARY]: a GUI e o runner usam este numero e a
    # lista de nomes para informar/quais servicos pararam na entrada do modo jogo.
    $stopped = 0
    $stoppedNames = @()

    foreach ($rec in $Recommendations) {
        Write-Host "[APPLY] $($rec.Name) | $($rec.CurrentStart) -> $($rec.ProposedStart) | Running: $($rec.CurrentStatus)"

        if ($Simulate) {
            if ($rec.ActionStop -and $rec.ProposedStart -ne "Automatic") { $stopped++; $stoppedNames += $rec.Name }
            continue
        }

        $startupTarget = Resolve-ServiceForStartupChange -Name $rec.Name
        if ($startupTarget -ne $rec.Name) {
            Write-Host "[INFO] Ajustando StartupType no servico-base: $startupTarget (origem: $($rec.Name))"
        }

        try {
            Set-Service -Name $startupTarget -StartupType $rec.ProposedStart -ErrorAction Stop
        }
        catch {
            Write-Warning "Falha ao configurar StartupType de $startupTarget (origem: $($rec.Name)): $($_.Exception.Message)"
            continue
        }

        if ($rec.ProposedStart -eq "Automatic" -and $rec.CurrentStatus -ne "Running") {
            try {
                Start-Service -Name $rec.Name -ErrorAction Stop
            }
            catch {
                Start-Service -Name $startupTarget -ErrorAction SilentlyContinue
            }
        }

        # So parar servicos que ficarao em Manual/Disabled (modo jogo).
        # No perfil normal (alvo Automatic) um servico rodando deve continuar rodando.
        if ($rec.ActionStop -and $rec.ProposedStart -ne "Automatic") {
            try {
                Stop-Service -Name $rec.Name -Force -ErrorAction Stop
                $stopped++
                $stoppedNames += $rec.Name
            }
            catch {
                Write-Warning "Falha ao parar $($rec.Name): $($_.Exception.Message)"
            }
        }
    }

    # names= Serviços parados (separados por |) - a GUI monta a aba "Modo Jogo"
    # com a lista de quais foram parados.
    Write-Host "[SUMMARY] stopped=$stopped;names=$($stoppedNames -join '|')"
}

function Rollback-FromBackup {
    param(
        [string]$Path,
        [switch]$Simulate
    )

    if (-not (Test-Path -Path $Path)) {
        throw "Arquivo de backup nao encontrado: $Path"
    }

    $backup = Get-Content -Path $Path -Raw | ConvertFrom-Json
    foreach ($svc in $backup.Services) {
        $startupType = Convert-StartModeToStartupType -StartMode $svc.StartMode
        $startupTarget = Resolve-ServiceForStartupChange -Name $svc.Name

        Write-Host "[ROLLBACK] $($svc.Name) -> $startupType | Estado original: $($svc.Status)"
        if ($startupTarget -ne $svc.Name) {
            Write-Host "[INFO] Rollback de StartupType no servico-base: $startupTarget (origem: $($svc.Name))"
        }

        if ($Simulate) {
            continue
        }

        try {
            Set-Service -Name $startupTarget -StartupType $startupType -ErrorAction Stop

            if ($svc.Status -eq "Running") {
                try {
                    Start-Service -Name $svc.Name -ErrorAction Stop
                }
                catch {
                    Start-Service -Name $startupTarget -ErrorAction SilentlyContinue
                }
            }
        }
        catch {
            Write-Warning "Falha no rollback de $($svc.Name): $($_.Exception.Message)"
        }
    }
}

if (-not (Test-Path -Path $ProfilePath) -and $Mode -ne "Rollback") {
    throw "Perfil nao encontrado: $ProfilePath"
}

# Admin exigido apenas quando havera alteracao real (simulacao WhatIf dispensa)
if (-not $WhatIf -and ($Mode -eq "Rollback" -or $Mode -eq "Apply") -and -not (Test-IsAdmin)) {
    throw "Execute o PowerShell como Administrador para usar os modos Apply/Rollback."
}

if ($Mode -eq "Rollback") {
    Rollback-FromBackup -Path $BackupPath -Simulate:$WhatIf
    return
}

$profile = Get-Content -Path $ProfilePath -Raw | ConvertFrom-Json
$snapshot = Get-ServiceSnapshot
$recommendations = @(Build-Recommendations -Snapshot $snapshot -Profile $profile)

if ($Mode -eq "Audit") {
    $activeServices = $snapshot |
        Where-Object { $_.Status -eq "Running" } |
        Sort-Object Name |
        Select-Object Name, DisplayName, StartMode, Status

    $summary = [PSCustomObject]@{
        Timestamp            = (Get-Date).ToString("s")
        Machine              = $env:COMPUTERNAME
        ActiveServiceCount   = $activeServices.Count
        ActiveServices       = $activeServices
        CandidateCount       = @($recommendations).Count
        Recommendations      = @($recommendations)
    }

    if ($IncludeProcessSnapshot) {
        $summary | Add-Member -MemberType NoteProperty -Name TopProcessesByCPU -Value (
            Get-Process | Sort-Object CPU -Descending | Select-Object -First 20 Name, Id, CPU, WorkingSet
        )

        $summary | Add-Member -MemberType NoteProperty -Name TopProcessesByMemory -Value (
            Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First 20 Name, Id, CPU, WorkingSet
        )
    }

    $summary | ConvertTo-Json -Depth 6
    return
}

if ($Mode -eq "Apply") {
    if ($WhatIf) {
        # Simulacao: mostra o que seria feito sem gravar backup nem alterar servicos
        Apply-Recommendations -Recommendations $recommendations -Simulate
        Write-Host "Simulacao concluida (WhatIf): nenhum servico foi alterado."
    }
    else {
        Save-Backup -Snapshot $snapshot -Path $BackupPath
        Apply-Recommendations -Recommendations $recommendations
        Write-Host "Aplicacao concluida. Backup salvo em: $BackupPath"
    }
}


