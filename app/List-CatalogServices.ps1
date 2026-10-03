# Exporta catalogo completo de servicos do Windows com classificacao de seguranca
# Formato: JSON com Name, DisplayName, Description, Status, StartMode, PathName, Category, IsCritical, Reason
param(
    [string]$ProfilePath = ".\service-profile.game.json"
)

$ErrorActionPreference = "SilentlyContinue"

# Carrega listas de servicos criticos e protegidos do perfil
$profile = if (Test-Path $ProfilePath) { Get-Content -Path $ProfilePath -Raw | ConvertFrom-Json } else { $null }

$criticalPrefixes = if ($profile) { @($profile.criticalNamePrefixes) } else { @(
    "WinDefend","WdNisSvc","SecurityHealthService","EventLog","RpcSs","DcomLaunch","PlugPlay",
    "Power","LanmanWorkstation","Dhcp","Dnscache","nsi","BFE","MpsSvc","CryptSvc","TrustedInstaller",
    "wuauserv","UsoSvc","BITS","TermService","ProfSvc","LSM","Schedule"
) }

$criticalDisplay = if ($profile) { @($profile.criticalDisplayContains) } else { @(
    "Windows Defender","Security","RPC","DHCP","DNS","Windows Update","Firewall","Cryptographic"
) }

$keepRunningPrefixes = if ($profile) { @($profile.keepRunningNamePrefixes) } else { @(
    "AudioEndpointBuilder","Audiosrv","WlanSvc","NlaSvc","Netman","SamSs","UserManager",
    "BrokerInfrastructure","StateRepository","AppXSvc","ClipSVC","Themes","FontCache",
    "TimeBrokerSvc","DPS","NVDisplay.ContainerLocalSystem"
) }

# Snapshot WMI com descricoes
$cim = Get-CimInstance Win32_Service | Select-Object Name, DisplayName, Description, StartMode, State, PathName
$byName = @{}
foreach ($item in $cim) {
    $byName[$item.Name] = $item
}

$services = Get-Service
$list = New-Object System.Collections.Generic.List[object]

foreach ($s in $services) {
    $name = [string]$s.Name
    $display = [string]$s.DisplayName
    $c = $byName[$name]

    $desc = if ($null -ne $c -and -not [string]::IsNullOrWhiteSpace($c.Description)) { [string]$c.Description } else { "" }
    $startMode = if ($null -ne $c) { [string]$c.StartMode } else { "Manual" }
    $state = if ($null -ne $c) { [string]$c.State } else { [string]$s.Status }
    $path = if ($null -ne $c) { [string]$c.PathName } else { "" }

    # Deteccao se e critico do sistema
    $isCritical = $false
    $reason = ""

    foreach ($p in $criticalPrefixes) {
        if ($name.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) {
            $isCritical = $true
            $reason = "Componente critico do Windows (seguranca/rede/kernel)"
            break
        }
    }
    if (-not $isCritical) {
        foreach ($d in $criticalDisplay) {
            if ($display.IndexOf($d, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $isCritical = $true
                $reason = "Servico essencial protegido ($d)"
                break
            }
        }
    }
    if (-not $isCritical) {
        foreach ($k in $keepRunningPrefixes) {
            if ($name.StartsWith($k, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isCritical = $true
                $reason = "Servico essencial de audio/interface/rede"
                break
            }
        }
    }

    $isWindows = $path.IndexOf("\Windows\", [System.StringComparison]::OrdinalIgnoreCase) -ge 0

    $category = if ($isCritical) {
        "Sistema Protegido"
    } elseif ($isWindows) {
        "Windows (Opcional)"
    } else {
        "Aplicativos / Terceiros"
    }

    $list.Add([PSCustomObject]@{
        Name        = $name
        DisplayName = $display
        Description = $desc
        Status      = $state
        StartMode   = $startMode
        Category    = $category
        IsCritical  = $isCritical
        Reason      = $reason
    })
}

$list | Select-Object Name, DisplayName, Description, Status, StartMode, Category, IsCritical, Reason | ConvertTo-Json -Depth 4 -Compress
