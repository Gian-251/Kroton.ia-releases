# Compila o KrotonIA.exe (WinForms, .NET Framework 4.x embutido no Windows)
# Uso: powershell -ExecutionPolicy Bypass -File app\Build-App.ps1
$ErrorActionPreference = 'Stop'

$appDir = $PSScriptRoot
$outExe = Join-Path (Split-Path -Parent $appDir) 'KrotonIA.exe'

# csc do .NET Framework (presente em todo Windows 10/11)
$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe do .NET Framework nao encontrado' }

$sources = @(
    (Join-Path $appDir 'Program.cs'),
    (Join-Path $appDir 'MainForm.cs'),
    (Join-Path $appDir 'MemoryPanel.cs'),
    (Join-Path $appDir 'ScriptRunner.cs'),
    (Join-Path $appDir 'KrotonTheme.cs'),
    (Join-Path $appDir 'ServiceDescriptionFetcher.cs'),
    (Join-Path $appDir 'GitHubUpdater.cs')
)
foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "Fonte ausente: $s" } }

$refs = @(
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll',
    '/r:System.Core.dll',
    '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll'
)

$manifest = Join-Path $appDir 'app.manifest'
$icon = Join-Path $appDir 'kroton.ico'
$logo = Join-Path $appDir 'kroton-logo.png'
foreach ($f in @($manifest, $icon, $logo)) {
    if (-not (Test-Path $f)) { throw "Recurso ausente: $f" }
}

$args = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32manifest:`"$manifest`"",
    "/win32icon:`"$icon`"",
    "/resource:`"$logo`",KrotonLogo.png",
    "/out:`"$outExe`""
) + $refs + $sources

Write-Host "Compilando com: $csc"
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Compilacao falhou (exit $LASTEXITCODE)" }

$exe = Get-Item $outExe
Write-Host ("OK: " + $exe.FullName + " (" + $exe.Length + " bytes)")

# Marcador local e especifico desta copia de desenvolvimento; nunca e empacotado.
$developerMarker = Join-Path $env:LOCALAPPDATA 'KrotonIA\developer-mode.txt'
$developerMarkerDir = Split-Path -Parent $developerMarker
if (-not (Test-Path $developerMarkerDir)) {
    New-Item -ItemType Directory -Path $developerMarkerDir -Force | Out-Null
}
[System.IO.File]::WriteAllText($developerMarker, (Split-Path -Parent $outExe))
Write-Host ("Modo desenvolvedor registrado localmente: " + $developerMarker)
