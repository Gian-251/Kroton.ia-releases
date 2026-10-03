<#
.SYNOPSIS
    Assina os artefatos de distribuicao do Kroton.ia com Authenticode.

.DESCRIPTION
    Usa signtool.exe (Windows SDK) para assinar KrotonIA.exe e cria um catalogo
    (.cat) assinado para os .bat/.ps1. Requer um certificado de assinatura de
    codigo (CodeSigningCert) no store do usuario ou um arquivo .pfx.

.PARAMETER CertThumbprint
    Thumbprint do certificado no store CurrentUser\My. Se omitido, usa o primeiro
    CodeSigningCert encontrado.

.PARAMETER PfxPath
    Caminho para arquivo .pfx (alternativa ao store). Requer PfxPassword.

.PARAMETER PfxPassword
    Senha do .pfx (SecureString).

.PARAMETER TimestampServer
    URL do servidor de timestamp (default: http://timestamp.digicert.com).

.EXAMPLE
    .\Sign-App.ps1 -CertThumbprint A049C2CB...
    .\Sign-App.ps1 -PfxPath .\kroton.pfx -PfxPassword (ConvertTo-SecureString "senha" -AsPlainText -Force)

.NOTES
    Para SmartScreen em maquinas novas, use um certificado EV Code Signing (~$200/ano)
    ou SignPath.io (gratuito para projetos open-source no GitHub).
#>
param(
    [string]$CertThumbprint,
    [string]$PfxPath,
    [securestring]$PfxPassword,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $rootDir "KrotonIA.exe"

# Localiza signtool.exe (Windows SDK)
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" -or $_.FullName -match "\\x86\\" } |
    Sort-Object FullName -Descending | Select-Object -First 1

if (-not $signtool) {
    Write-Error "signtool.exe nao encontrado. Instale o Windows SDK."
    exit 1
}

Write-Host "signtool: $($signtool.FullName)" -ForegroundColor Cyan

# Seleciona o certificado
$cert = $null
if ($PfxPath) {
    if (-not $PfxPassword) { Write-Error "PfxPassword necessario para PfxPath"; exit 1 }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword)
    Write-Host "Certificado carregado de PFX: $($cert.Subject)" -ForegroundColor Cyan
} elseif ($CertThumbprint) {
    $cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint" -ErrorAction Stop
    Write-Host "Certificado do store: $($cert.Subject)" -ForegroundColor Cyan
} else {
    $cert = Get-ChildItem "Cert:\CurrentUser\My" -CodeSigningCert | Select-Object -First 1
    if (-not $cert) { Write-Error "Nenhum certificado CodeSigningCert encontrado no store"; exit 1 }
    Write-Host "Certificado auto-selecionado: $($cert.Subject)" -ForegroundColor Cyan
}

# Assina o exe
Write-Host "`nAssinando KrotonIA.exe..." -ForegroundColor Yellow
& $signtool.FullName sign /sha1 $cert.Thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $exePath
if ($LASTEXITCODE -ne 0) { Write-Error "Falha ao assinar exe"; exit 1 }

# Verifica a assinatura
$sig = Get-AuthenticodeSignature $exePath
Write-Host "Status: $($sig.Status) - $($sig.StatusMessage)" -ForegroundColor $(if ($sig.Status -eq "Valid") { "Green" } else { "Red" })

# Cria catalogo para .bat/.ps1 (opcional - melhora integridade mas nao remove SmartScreen)
Write-Host "`nCriando catalogo para scripts..." -ForegroundColor Yellow
$catPath = Join-Path $rootDir "KrotonScripts.cat"
$scripts = @(
    (Join-Path $rootDir "install\Install-Kroton.bat"),
    (Join-Path $rootDir "install\Uninstall-Kroton.bat"),
    (Join-Path $rootDir "KrotonGameModeRunner.ps1"),
    (Join-Path $rootDir "Optimize-BackgroundServices.ps1")
) | Where-Object { Test-Path $_ }

if ($scripts.Count -gt 0) {
    $infPath = Join-Path $env:TEMP "KrotonScripts.inf"
    $inf = "[CatalogHeader]`nName=KrotonScripts.cat`nResultDir=.`n[CatalogFiles]`n"
    $scripts | ForEach-Object { $inf += "File$_=$($_.Replace($rootDir, '.'))`n" }
    Set-Content $infPath $inf -Encoding ASCII
    
    # makecat.exe cria o catalogo (parte do Windows SDK)
    $makecat = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter "makecat.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName -Descending | Select-Object -First 1
    
    if ($makecat) {
        & $makecat.FullName $infPath
        if (Test-Path $catPath) {
            & $signtool.FullName sign /sha1 $cert.Thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $catPath
            Write-Host "Catalogo assinado: $catPath" -ForegroundColor Green
        }
    } else {
        Write-Warning "makecat.exe nao encontrado - catalogo nao criado"
    }
    Remove-Item $infPath -Force -ErrorAction SilentlyContinue
}

Write-Host "`nAssinatura concluida." -ForegroundColor Green
Write-Host "NOTA: Certificados auto-assinados nao removem avisos SmartScreen em maquinas novas." -ForegroundColor Yellow
Write-Host "Para distribuicao publica, use SignPath.io (gratuito OSS) ou EV Code Signing Cert." -ForegroundColor Yellow
