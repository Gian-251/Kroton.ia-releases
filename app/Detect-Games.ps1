$ErrorActionPreference = 'SilentlyContinue'

# Ignora executaveis que nunca sao o jogo principal (desinstaladores, runtimes, crash handlers)
$skipExePattern = 'unins|uninstall|setup|redist|vcredist|dxsetup|crashreport|unitycrash|easyanticheat'

# Melhor palpite de nome de processo (sem .exe) para um jogo:
# maior executavel de primeiro nivel da pasta de instalacao. Retorna $null se nao houver candidato.
function Get-GameProcessGuess {
  param([string]$Dir)

  if ([string]::IsNullOrWhiteSpace($Dir)) { return $null }
  if (-not (Test-Path -LiteralPath $Dir)) { return $null }

  $best = $null
  $exes = @(Get-ChildItem -LiteralPath $Dir -Filter '*.exe' -File -ErrorAction SilentlyContinue)
  if ($exes.Count -eq 0) {
    # Alguns jogos guardam o executavel em subpasta (ex.: bin\win_x64, Game\Binaries\Win64)
    $exes = @(Get-ChildItem -LiteralPath $Dir -Filter '*.exe' -File -Recurse -Depth 3 -ErrorAction SilentlyContinue)
  }
  foreach ($e in $exes) {
    if ($e.BaseName -match $skipExePattern) { continue }
    if (($null -eq $best) -or ($e.Length -gt $best.Length)) { $best = $e }
  }

  if ($null -ne $best) { return $best.BaseName }
  return $null
}


function Get-UninstallApps {
  $paths = @(
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
  )
  foreach ($p in $paths) {
    Get-ItemProperty $p |
      Where-Object { $_.DisplayName } |
      Select-Object DisplayName, InstallLocation
  }
}

function Get-SteamGames {
  $results = @()
  $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
  if (-not $steamPath) { return $results }

  $libs = @($steamPath)
  $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    $txt = Get-Content $vdf -Raw -Encoding UTF8
    $pathMatches = [regex]::Matches($txt, '"path"\s+"([^"]+)"')
    foreach ($m in $pathMatches) {
      # vdf escapa barra invertida (D:\\Steam) - desserializa para um caminho real
      $p = $m.Groups[1].Value -replace '\\\\', '\'
      if ($p -and -not ($libs -contains $p)) { $libs += $p }
    }
  }

  foreach ($lib in $libs) {
    $steamapps = Join-Path $lib 'steamapps'
    $manifests = Get-ChildItem $steamapps -Filter 'appmanifest_*.acf'
    foreach ($mf in $manifests) {
      $raw = Get-Content $mf.FullName -Raw -Encoding UTF8
      $name = ([regex]::Match($raw, '"name"\s+"([^"]+)"')).Groups[1].Value
      if ($name) {
        # installdir do manifesto = pasta real do jogo dentro de steamapps\common
        $dirName = ([regex]::Match($raw, '"installdir"\s+"([^"]+)"')).Groups[1].Value
        $gameDir = if ($dirName) { Join-Path (Join-Path $steamapps 'common') $dirName } else { Join-Path $steamapps 'common' }
        $results += [PSCustomObject]@{
          Name = $name
          Source = 'Steam'
          Path = $gameDir
          ProcessName = Get-GameProcessGuess -Dir $gameDir
        }
      }
    }
  }

  return $results
}

function Get-EpicGames {
  $results = @()
  $manifestDir = 'C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests'
  if (-not (Test-Path $manifestDir)) { return $results }
  Get-ChildItem $manifestDir -Filter '*.item' | ForEach-Object {
    try {
      $j = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
      if ($j.DisplayName) {
        $results += [PSCustomObject]@{
          Name = $j.DisplayName
          Source = 'Epic'
          Path = $j.InstallLocation
          ProcessName = Get-GameProcessGuess -Dir $j.InstallLocation
        }
      }
    } catch {}
  }
  return $results
}

function Get-RiotGames {
  $results = @()
  $base = 'C:\Riot Games'
  if (Test-Path $base) {
    Get-ChildItem $base -Directory | ForEach-Object {
      $results += [PSCustomObject]@{
        Name = $_.Name
        Source = 'Riot'
        Path = $_.FullName
        ProcessName = Get-GameProcessGuess -Dir $_.FullName
      }
    }
  }
  return $results
}

$keywords = @('minecraft','valorant','fortnite','league','dota','counter-strike','cs2','apex','gta','rocket league','overwatch','rainbow','elden','witcher','fifa','call of duty','warzone','diablo','halo','game','jogo')

$apps = Get-UninstallApps | Where-Object {
  $n = [string]$_.DisplayName
  foreach ($k in $keywords) {
    if ($n.ToLower().Contains($k)) { return $true }
  }
  return $false
} | ForEach-Object {
  [PSCustomObject]@{
    Name = $_.DisplayName
    Source = 'InstalledApps'
    Path = $_.InstallLocation
    ProcessName = Get-GameProcessGuess -Dir $_.InstallLocation
  }
}

$all = @()
$all += $apps
$all += Get-SteamGames
$all += Get-EpicGames
$all += Get-RiotGames

$all = @($all | Where-Object { $_ -and $_.Name } | Sort-Object Name -Unique)

# runtime resolvido pelo LOCAL DO SCRIPT (repo: <raiz>\runtime, instalado: <InstallPath>\runtime)
# para bater com ScriptRunner.BaseDir usado pela GUI
$rootDir = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($rootDir)) { $rootDir = (Get-Location).Path }
New-Item -ItemType Directory -Path (Join-Path $rootDir 'runtime') -Force | Out-Null
$outPath = Join-Path $rootDir 'runtime\games-detected.json'

# -InputObject garante JSON array mesmo com 0 ou 1 item (o GUI espera IList)
ConvertTo-Json -InputObject @($all) -Depth 5 | Set-Content -Path $outPath -Encoding UTF8

"TOTAL_DETECTADOS=$($all.Count)"
$all | Select-Object Name,Source,Path | Format-Table -AutoSize
