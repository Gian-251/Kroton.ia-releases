param(
    [ValidateSet('Guard', 'Report')]
    [string]$Mode = 'Guard',

    [int]$MinFreeMb = 0,       # 0 = usa memoryGuard.minFreeMb do config
    [int]$MinProcessMb = 0,    # 0 = usa memoryGuard.minProcessMb do config

    [int]$KillRamPercent = 0,  # 0 = usa memoryGuard.killRamPercent (97)
    [string[]]$KillNames = @(), # alvo restrito (teste/debug); vazio = usa memoryGuard.targetNames (ou qualquer elegivel)

    [int]$Top = 15,

    [switch]$Force,            # ignora o limite de memoria livre
    [switch]$DryRun,           # so mostra o que faria; nao mexe em nada
    [string[]]$GameNames = @()
)

$ErrorActionPreference = 'Stop'

# Raiz = pasta pai de app\ (mesmo contrato da GUI e do runner)
$rootDir = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($rootDir)) { $rootDir = (Get-Location).Path }
$runtimeDir = Join-Path $rootDir 'runtime'
if (-not (Test-Path $runtimeDir)) { New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null }
$memLogPath = Join-Path $runtimeDir 'memory-guard.log'
$configPath = Join-Path $rootDir 'game-mode-config.json'

function Get-JsonProp {
    param($Obj, [string]$Name, $Default)
    if ($null -ne $Obj -and $Obj.PSObject.Properties[$Name]) {
        $v = $Obj.PSObject.Properties[$Name].Value
        if ($null -ne $v) { return $v }
    }
    return $Default
}

# ---------- config ----------
$cfg = $null
$cfgGuard = $null
try {
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $cfgGuard = Get-JsonProp $cfg 'memoryGuard' $null
    }
}
catch { $cfg = $null; $cfgGuard = $null }

if ($MinFreeMb -le 0)     { $MinFreeMb = [int](Get-JsonProp $cfgGuard 'minFreeMb' 1024) }
if ($MinProcessMb -le 0)  { $MinProcessMb = [int](Get-JsonProp $cfgGuard 'minProcessMb' 300) }
if ($MinProcessMb -lt 50) { $MinProcessMb = 50 }

# ---------- Memory Pressure (gates de disparo) ----------
# pressurePercent: limpa apenas quando o uso de RAM atinge X% (0 = so o minFreeMb)
# stallCheck/stallPageInputMin: override de frametime instavel - so com jogo da lista
# rodando E o SO pagando para disco (hard page faults/s) acima do limiar
$pressurePercent = [int](Get-JsonProp $cfgGuard 'pressurePercent' 90)
if ($pressurePercent -lt 0)   { $pressurePercent = 0 }
if ($pressurePercent -gt 100) { $pressurePercent = 100 }
$stallCheck = [bool](Get-JsonProp $cfgGuard 'stallCheck' $true)
$stallPageInputMin = [int](Get-JsonProp $cfgGuard 'stallPageInputMin' 50)
if ($stallPageInputMin -lt 1) { $stallPageInputMin = 1 }

$priorityNames = @(Get-JsonProp $cfgGuard 'priorityNames' @('Discord', 'Spotify', 'explorer', 'audiodg', 'KrotonIA', 'MsMpEng'))
if ($GameNames.Count -eq 0) {
    $cfgGames = Get-JsonProp $cfg 'gameProcessNames' @()
    $GameNames = @($cfgGames | ForEach-Object { [string]$_ } | Where-Object { $_ })
}

# Listas de priorizacao (whitelist x alvos) - recarregadas a cada execucao:
# - whitelistNames: apps criticos extras que NUNCA sao tocados (soma ao priorityNames)
# - targetNames: se NAO vazio, SOMENTE esses nomes entram no trim/kill (vazio = qualquer elegivel)
# Ambos aceitam curinga (*) no nome do processo.
$whitelistNames = @(Get-JsonProp $cfgGuard 'whitelistNames' @() | ForEach-Object { [string]$_ } | Where-Object { $_ })
$targetNames = @(Get-JsonProp $cfgGuard 'targetNames' @() | ForEach-Object { [string]$_ } | Where-Object { $_ })

# Casa o nome do processo com a lista (curinga via -like); lista vazia = nao casa nada.
function Test-NameList {
    param([string]$ProcName, [string[]]$List)
    foreach ($n in $List) { if ($n -and $ProcName -like $n) { return $true } }
    return $false
}

# Gate de frametime (Memory Pressure): o SO pagando paginas para DISCO (hard faults)
# com o jogo da lista rodando = stutter = frametime instavel, mesmo com RAM "folgada".
# e' o proximo causal disponivel sem dependencia externa (PresentMon/ETW).
function Test-StallGate {
    if (-not $stallCheck -or $GameNames.Count -eq 0) { return $false }
    $running = Get-Process -Name $GameNames -ErrorAction SilentlyContinue
    if (-not $running) { return $false }
    try {
        $perf = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory -ErrorAction Stop
        return ([int]$perf.PagesInputPersec -ge $stallPageInputMin)
    }
    catch { return $false }
}

# ---------- helper nativo (trim + sessoes de audio via Core Audio) ----------
if (-not ('KrotonMem.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KrotonMem
{
    public static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr process);

        // Libera as paginas residentes do processo (nao mata, so devolve RAM ao SO).
        public static bool Trim(int pid)
        {
            const uint QUERY = 0x0400;   // PROCESS_QUERY_INFORMATION
            const uint QUOTA = 0x0100;    // PROCESS_SET_QUOTA
            IntPtr h = OpenProcess(QUERY | QUOTA, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                return EmptyWorkingSet(h);
            }
            finally { CloseHandle(h); }
        }
        // PIDs com sessao de audio ATIVA (Core Audio) - captura o navegador que
        // esta tocando musica/video, Discord, Spotify etc.
        // Ordem dos slots de IAudioSessionControl2 segue audiopolicy.h:
        // base tem 9 metodos (GetState..UnregisterAudioSessionNotification) e
        // GetProcessId e o 12o (slot 11) - declarar a menos causa AccessViolation.
        public static List<uint> ActiveAudioPids()
        {
            List<uint> result = new List<uint>();
            IMMDevice device = null;
            object mgrObj = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
                if (hr != 0 || device == null) return result;

                Guid iidMgr = typeof(IAudioSessionManager2).GUID;
                hr = device.Activate(ref iidMgr, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out mgrObj);
                if (hr != 0 || mgrObj == null) return result;

                IAudioSessionManager2 mgr = (IAudioSessionManager2)mgrObj;
                hr = mgr.GetSessionEnumerator(out sessions);
                if (hr != 0 || sessions == null) return result;

                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 ctl = null;
                    try
                    {
                        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;

                        int state;
                        if (ctl.GetState(out state) != 0 || state != 1 /*Active*/) continue;

                        uint pid;
                        if (ctl.GetProcessId(out pid) == 0 && pid > 0 && !result.Contains(pid))
                            result.Add(pid);
                    }
                    finally
                    {
                        if (ctl != null) Marshal.ReleaseComObject(ctl);
                    }
                }
            }
            catch { }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (mgrObj != null) Marshal.ReleaseComObject(mgrObj);
                if (device != null) Marshal.ReleaseComObject(device);
            }
            return result;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr props);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid guid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(ref Guid guid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    // ATENCAO: a ordem dos metodos precisa bater 1:1 com audiopolicy.h (Windows SDK).
    // IAudioSessionControl tem 9 metodos (inclui SetDisplayName/SetIconPath/
    // SetGroupingParam) e IAudioSessionControl2 acrescenta 5 - GetProcessId e o
    // 12o (slot 11). Qualquer slot errado = AccessViolation no processo.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }
}
'@ -Language CSharp -ErrorAction Stop
}

function Get-MemoryInfo {
    $os = Get-CimInstance Win32_OperatingSystem
    $total = [int][math]::Round($os.TotalVisibleMemorySize / 1KB)
    $free = [int][math]::Round($os.FreePhysicalMemory / 1KB)
    [PSCustomObject]@{
        totalMb = $total
        freeMb  = $free
        usedMb  = ($total - $free)
    }
}

function Get-ProcessWs {
    $list = @()
    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        $ws = 0
        try { $ws = [int][math]::Round($p.WorkingSet64 / 1MB) } catch { $ws = 0 }
        if ($ws -gt 0) {
            $list += [PSCustomObject]@{ Id = [int]$p.Id; Name = [string]$p.Name; WsMb = $ws }
        }
    }
    return , $list
}

# ---------- Report: estado atual em JSON (teste / consumo externo) ----------
if ($Mode -eq 'Report') {
    $m = Get-MemoryInfo
    $procs = Get-ProcessWs
    # nome sem colisao com o parametro $Top (mesma variavel ignorando maiusculas)
    $topList = @($procs | Sort-Object WsMb -Descending | Select-Object -First $Top)
    $report = [PSCustomObject]@{
        totalMb = $m.totalMb
        freeMb  = $m.freeMb
        usedMb  = $m.usedMb
        percent = [int][math]::Round(($m.usedMb / [math]::Max(1, $m.totalMb)) * 100)
        top     = @($topList | ForEach-Object {
            [PSCustomObject]@{ name = $_.Name; pid = $_.Id; mb = $_.WsMb }
        })
    }
    ConvertTo-Json -InputObject $report -Depth 5
    return
}

# ---------- Guard: libera memoria (modo jogo do runner / botao da GUI) ----------
$mem = Get-MemoryInfo
$usedPct = 0
if ($mem.totalMb -gt 0) { $usedPct = [int][math]::Round(($mem.usedMb * 100.0) / $mem.totalMb) }

# KILL: encerra processos elegiveis quando a RAM esta em colapso ( travando ).
# Default OFF no script (instalacoes antigas sem a chave nao mudam de comportamento);
# o game-mode-config.json do repo habilita com killEnabled=true.
$killEnabled = [bool](Get-JsonProp $cfgGuard 'killEnabled' $false)
if ($KillRamPercent -le 0) { $KillRamPercent = [int](Get-JsonProp $cfgGuard 'killRamPercent' 97) }
if ($KillRamPercent -lt 1) { $KillRamPercent = 1 }
$killMaxPerCycle = [int](Get-JsonProp $cfgGuard 'killMaxPerCycle' 3)
if ($killMaxPerCycle -lt 1) { $killMaxPerCycle = 1 }
$killGate = $killEnabled -and $usedPct -ge $KillRamPercent

# ---------- Memory Pressure: limpa APENAS sob pressao ----------
# pressao = uso de RAM >= pressurePercent% OU livre < minFreeMb; se a RAM parece
# folgada, o stall gate (frametime instavel: hard faults do SO + jogo rodando) ainda
# pode disparar. -Force (botao da GUI) e kill armado ignoram o gate (como antes).
$trigger = ''
if ($Force) { $trigger = 'forca' }
elseif ($killGate) { $trigger = 'kill' }
else {
    $underPressure = ($mem.freeMb -lt $MinFreeMb) -or
        ($pressurePercent -gt 0 -and $usedPct -ge $pressurePercent)
    if ($underPressure) { $trigger = 'pressao' }
    elseif (Test-StallGate) { $trigger = 'stall' }
    else {
        Write-Output "MEM_FREE_OK=$($mem.freeMb)MB ram=${usedPct}%"
        return
    }
}

# Conjunto de PIDs que NUNCA sao tocados
$protected = New-Object 'System.Collections.Generic.HashSet[int]'
[void]$protected.Add(0)
[void]$protected.Add(4)

$criticalNames = @('System', 'Registry', 'smss', 'csrss', 'wininit', 'winlogon', 'services',
    'lsass', 'svchost', 'dwm', 'audiodg', 'fontdrvhost', 'Memory Compression', 'MsMpEng',
    'SecurityHealthService', 'SearchIndexer', 'SecurityHealthSystrag', 'WmiPrvSE')
$selfNames = @('powershell', 'pwsh', 'cmd', 'conhost', 'WindowsTerminal', 'wscript', 'cscript', 'KrotonIA')

$procs = Get-ProcessWs

# 1) criticos do SO + terminal/Kroton
foreach ($p in $procs) {
    if ($criticalNames -contains $p.Name -or $selfNames -contains $p.Name) {
        [void]$protected.Add($p.Id)
    }
}

# 2) processos do jogo (prioridade maxima enquanto o modo jogo estiver ativo)
foreach ($p in $procs) {
    foreach ($g in $GameNames) {
        $gName = $g -replace '\.exe$', ''
        if ($gName -and $p.Name -eq $gName) { [void]$protected.Add($p.Id); break }
    }
}

# 3) whitelist: prioridades (priorityNames) + lista de exclusao (whitelistNames)
#    - aceita curinga; NUNCA sai do conjunto de protegidos
$whitelistAll = @($priorityNames) + @($whitelistNames)
foreach ($p in $procs) {
    if (Test-NameList $p.Name $whitelistAll) { [void]$protected.Add($p.Id) }
}

# 4) processos com sessao de audio ATIVA (navegador tocando musica/video etc.)
#    - verificacao de estado de audio via API do Windows (Core Audio): so protege
#      enquanto a sessao estiver em estado Active; para de tocar = volta a ser elegivel.
$audioCount = 0
try {
    foreach ($audioPid in [KrotonMem.Native]::ActiveAudioPids()) {
        [void]$protected.Add([int]$audioPid)
        $audioCount++
    }
}
catch { }

# 5) processo PRINCIPAL de navegadores: os filhos (renderizadores das demais
#    paginas) continuam elegiveis - e e isso que reduz o consumo sem derrubar
#    o processo que esta emitindo audio
$browserNames = @('chrome', 'msedge', 'msedgewebview2', 'firefox', 'brave', 'opera',
    'opera_gx', 'vivaldi', 'iexplore', 'waterfox')
$wmi = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
$parentOf = @{}
$nameOf = @{}
foreach ($w in $wmi) {
    $parentOf[[int]$w.ProcessId] = [int]$w.ParentProcessId
    $nameOf[[int]$w.ProcessId] = ([string]$w.Name) -replace '\.exe$', ''
}
foreach ($w in $wmi) {
    $base = $nameOf[[int]$w.ProcessId]
    if ($browserNames -notcontains $base) { continue }
    $parentPid = [int]$w.ParentProcessId
    $parentBase = $null
    if ($nameOf.ContainsKey($parentPid)) { $parentBase = $nameOf[$parentPid] }
    if ($parentBase -ne $base) { [void]$protected.Add([int]$w.ProcessId) }
}

# Candidatos: grandes, nao protegidos e DENTRO da lista de alvos quando houver
# (memoryGuard.targetNames vazio = qualquer elegivel; tampa de 12 por execucao
#  para nao provocar uma onda massiva de page faults)
$candidates = @($procs |
    Where-Object {
        -not $protected.Contains($_.Id) -and $_.WsMb -ge $MinProcessMb -and
        ($targetNames.Count -eq 0 -or (Test-NameList $_.Name $targetNames))
    } |
    Sort-Object WsMb -Descending |
    Select-Object -First 12)

$freedMb = 0
$count = 0
$details = @()

foreach ($c in $candidates) {
    if ($DryRun) {
        $details += "$($c.Name)($($c.Id))=$($c.WsMb)MB"
        continue
    }

    $before = $c.WsMb
    $trimmed = $false
    try { $trimmed = [KrotonMem.Native]::Trim($c.Id) } catch { $trimmed = $false }
    if (-not $trimmed) { continue }

    Start-Sleep -Milliseconds 40
    $after = $before
    try {
        $proc = Get-Process -Id $c.Id -ErrorAction Stop
        $after = [int][math]::Round($proc.WorkingSet64 / 1MB)
    }
    catch { $after = 0 }   # processo morreu no meio da medicao

    $saved = $before - $after
    if ($saved -gt 0) {
        $freedMb += $saved
        $count++
        $details += "$($c.Name)($($c.Id))-$saved" + "MB"
    }
}

# ---------- KILL: encerra processos elegiveis quando a RAM esta em colapso ----------
# Protecoes ALEM das acima: SO processos SEM janela visivel (nunca derrubar um app
# com trabalho aberto - Word, IDE etc.) e tampa killMaxPerCycle por execucao.
# -KillNames restringe o alvo (uso em teste/debug); vazio = qualquer elegivel.
function Close-ProcessGracefully {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$GraceMs = 2000
    )

    if ($null -eq $Process) { return $false }
    if ($Process.MainWindowHandle -eq 0) { return $false }

    try {
        $closed = $Process.CloseMainWindow()
        if (-not $closed) { return $false }
    }
    catch {
        return $false
    }

    $elapsed = 0
    while ($elapsed -lt $GraceMs) {
        try {
            if ($Process.HasExited) { return $true }
        }
        catch {
            return $true
        }

        Start-Sleep -Milliseconds 200
        $elapsed += 200
    }

    try {
        if (-not $Process.HasExited) { return $false }
    }
    catch {
        return $true
    }

    return $true
}

$killCount = 0
$killMb = 0
$killDetails = @()
if ($killGate) {
    $noWindow = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        try { if ($p.MainWindowHandle -eq 0) { [void]$noWindow.Add([int]$p.Id) } } catch { }
    }

    $killPool = @($procs | Where-Object {
        -not $protected.Contains($_.Id) -and $noWindow.Contains($_.Id)
    })
    if ($KillNames.Count -gt 0) {
        # Alvo explicito (teste/debug): o NOME manda (sem exigir MinProcessMb);
        # protecoes acima continuam valendo.
        $killPool = @($killPool | Where-Object { Test-NameList $_.Name $KillNames })
    }
    else {
        $killPool = @($killPool | Where-Object { $_.WsMb -ge $MinProcessMb })
        # Lista de alvos do config: se definida, somente ela e' elegivel p/ kill
        if ($targetNames.Count -gt 0) {
            $killPool = @($killPool | Where-Object { Test-NameList $_.Name $targetNames })
        }
    }
    $killPool = @($killPool | Sort-Object WsMb -Descending | Select-Object -First $killMaxPerCycle)

    foreach ($c in $killPool) {
        if ($DryRun) { $killDetails += "$($c.Name)($($c.Id))=$($c.WsMb)MB"; continue }

        try {
            $targetProc = Get-Process -Id $c.Id -ErrorAction Stop
            $gracefulExit = Close-ProcessGracefully -Process $targetProc
            if (-not $gracefulExit) {
                Stop-Process -Id $c.Id -Force -ErrorAction Stop
            }

            $killCount++
            $killMb += $c.WsMb
            $killDetails += "$($c.Name)($($c.Id))=$($c.WsMb)MB"
        }
        catch { }  # acesso negado ou ja terminou por conta propria - continua
    }
}

$suffix = ""
if ($DryRun) { $suffix = " [DRYRUN " + ($details -join ', ') + "]" }
elseif ($details.Count -gt 0) { $suffix = " | " + ($details -join ', ') }

$summary = "$(Get-Date -Format s) [MEM] gatilho=$trigger livre=$($mem.freeMb)MB limiar=${MinFreeMb}MB " +
    "protegidos=$($protected.Count) audio=$audioCount trim=$count liberado=${freedMb}MB" + $suffix

$logLines = @()
if (-not $DryRun -and $count -gt 0) { $logLines += $summary }

if ($killDetails.Count -gt 0) {
    $killSuffix = if ($DryRun) { " [DRYRUN " + ($killDetails -join ', ') + "]" } else { " | " + ($killDetails -join ', ') }
    $killLine = "$(Get-Date -Format s) [KILL] ram=${usedPct}% limiar=${KillRamPercent}% " +
        "encerrados=$killCount liberado=${killMb}MB" + $killSuffix
    if (-not $DryRun -and $killCount -gt 0) { $logLines += $killLine }
    if ($DryRun) { Write-Output $killLine }
}

foreach ($line in $logLines) {
    Add-Content -Path $memLogPath -Value $line -Encoding UTF8
}
# mantem o log enxuto (rodando a cada ciclo do runner ele cresce rapido)
if ((Test-Path $memLogPath) -and (Get-Item $memLogPath).Length -gt 1MB) {
    $tail = Get-Content $memLogPath -Tail 500
    Set-Content -Path $memLogPath -Value $tail -Encoding UTF8
}

if ($DryRun) { Write-Output $summary }
# MEM_FREED_MB/MEM_PROCS mantidos na frente (regex da GUI); kill e' sufixo.
Write-Output "MEM_FREED_MB=$freedMb;MEM_PROCS=$count;MEM_KILLED=$killCount;MEM_KILLED_MB=$killMb"





