@echo off
REM ============================================
REM Kroton.ia Uninstaller (wrapper do Uninstall-Kroton.ps1)
REM ============================================

net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERRO] Este desinstalador requer DIREITOS DE ADMINISTRADOR
    echo.
    pause
    exit /b 1
)

setlocal EnableExtensions

cls
echo.
echo =====================================
echo   KROTON.IA UNINSTALLER
echo =====================================
echo.
echo Tem certeza que deseja DESINSTALAR Kroton.ia?
echo.
set /p "CONFIRM=Digite 'sim' para confirmar: "

if /i not "%CONFIRM%"=="sim" (
    echo Desinstalacao cancelada.
    exit /b 0
)

set "UNINSTALL_PS1=%~dp0Uninstall-Kroton.ps1"

if not exist "%UNINSTALL_PS1%" (
    echo [ERRO] Uninstall-Kroton.ps1 nao encontrado ao lado deste arquivo.
    pause
    exit /b 1
)

PowerShell -NoProfile -ExecutionPolicy Bypass -File "%UNINSTALL_PS1%"

echo.
pause
exit /b 0
