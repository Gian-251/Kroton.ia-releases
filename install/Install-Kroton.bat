@echo off
REM ============================================
REM Kroton.ia Installer
REM ============================================
setlocal EnableExtensions EnableDelayedExpansion

if /I not "%KROTON_SKIP_ADMIN_CHECK%"=="1" (
    net session >nul 2>&1
    if %ERRORLEVEL% neq 0 (
        echo.
        echo [ERRO] Este instalador requer DIREITOS DE ADMINISTRADOR
        echo.
        pause
        exit /b 1
    )
)

set "INSTALL_PATH=%ProgramFiles%\KrotonIA"
set "SCRIPT_DIR=%~dp0"
set "TASK_NAME=KrotonIA-GameModeRunner"
set "SERVICE_NAME=KrotonIAService"

if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

rem Origem dos arquivos: mesma pasta (instalacao plana / pacote extraido)
rem ou pasta pai (repositorio: scripts em install\, runtime na raiz)
set "SOURCE_DIR=%SCRIPT_DIR%"
if not exist "%SOURCE_DIR%\KrotonIA.exe" for %%I in ("%SCRIPT_DIR%\..") do set "SOURCE_DIR=%%~fI"

call :ValidateSourceFiles
if errorlevel 1 exit /b 1

:MENU
cls
echo.
echo =====================================
echo   KROTON.IA GAME MODE OPTIMIZER v1.1
echo =====================================
echo.
echo Origem : !SOURCE_DIR!
echo Destino: !INSTALL_PATH!
echo.
echo [1] Instalacao padrao + tarefa agendada SYSTEM
echo [2] Instalacao + tarefa agendada por usuario
echo [3] Instalacao + servico Windows
echo [4] Instalar somente os arquivos
echo [5] Audit do Optimize-BackgroundServices
echo [6] Aplicar perfil GAME agora
echo [7] Restaurar estado original (rollback)
echo [8] Sair
echo.
set /p "MENU_OPTION=Escolha uma opcao: "

if "%MENU_OPTION%"=="1" goto OPTION_1
if "%MENU_OPTION%"=="2" goto OPTION_2
if "%MENU_OPTION%"=="3" goto OPTION_3
if "%MENU_OPTION%"=="4" goto OPTION_4
if "%MENU_OPTION%"=="5" goto OPTION_5
if "%MENU_OPTION%"=="6" goto OPTION_6
if "%MENU_OPTION%"=="7" goto OPTION_7
if "%MENU_OPTION%"=="8" exit /b 0

echo.
echo [ERRO] Opcao invalida.
pause
goto MENU

:OPTION_1
call :ChooseInstallPath
if errorlevel 1 goto MENU
call :InstallCore
if errorlevel 1 goto OPERATION_FAILED
call :RemoveServiceIfPresent
call :RegisterSystemTask
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Instalacao concluida com tarefa agendada SYSTEM."
goto OPERATION_SUCCESS

:OPTION_2
call :ChooseInstallPath
if errorlevel 1 goto MENU
call :InstallCore
if errorlevel 1 goto OPERATION_FAILED
call :RemoveServiceIfPresent
call :RegisterStartupTask
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Instalacao concluida com tarefa por usuario."
goto OPERATION_SUCCESS

:OPTION_3
call :ChooseInstallPath
if errorlevel 1 goto MENU
call :InstallCore
if errorlevel 1 goto OPERATION_FAILED
call :RegisterWindowsService
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Instalacao concluida com servico Windows."
goto OPERATION_SUCCESS

:OPTION_4
call :ChooseInstallPath
if errorlevel 1 goto MENU
call :InstallCore
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Arquivos instalados sem registrar inicializacao automatica."
goto OPERATION_SUCCESS

:OPTION_5
call :ResolveWorkDir
PowerShell -NoProfile -ExecutionPolicy Bypass -File "!WORK_DIR!\Optimize-BackgroundServices.ps1" -Mode Audit -ProfilePath "!WORK_DIR!\service-profile.json" -IncludeProcessSnapshot
if errorlevel 1 goto OPERATION_FAILED
pause
goto MENU

:OPTION_6
call :ResolveWorkDir
PowerShell -NoProfile -ExecutionPolicy Bypass -File "!WORK_DIR!\Optimize-BackgroundServices.ps1" -Mode Apply -ProfilePath "!WORK_DIR!\service-profile.game.json" -BackupPath "!WORK_DIR!\backup\services-backup-game.json"
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Perfil GAME aplicado com sucesso."
goto OPERATION_SUCCESS

:OPTION_7
call :ResolveWorkDir
if exist "!WORK_DIR!\backup\services-backup-game.json" (
    PowerShell -NoProfile -ExecutionPolicy Bypass -File "!WORK_DIR!\Optimize-BackgroundServices.ps1" -Mode Rollback -BackupPath "!WORK_DIR!\backup\services-backup-game.json"
) else (
    PowerShell -NoProfile -ExecutionPolicy Bypass -File "!WORK_DIR!\Optimize-BackgroundServices.ps1" -Mode Apply -ProfilePath "!WORK_DIR!\service-profile.normal.json" -BackupPath "!WORK_DIR!\backup\services-backup-normal.json"
)
if errorlevel 1 goto OPERATION_FAILED
set "SUCCESS_MESSAGE=Estado original restaurado."
goto OPERATION_SUCCESS

:ChooseInstallPath
set "PICKER_FILE=%TEMP%\KrotonIA-InstallPath-%RANDOM%-%RANDOM%.txt"
set "KROTON_PICKER_FILE=!PICKER_FILE!"
if exist "!PICKER_FILE!" del /q "!PICKER_FILE!"
PowerShell -NoProfile -STA -ExecutionPolicy Bypass -Command ^
 "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.Windows.Forms;" ^
 "$d=New-Object System.Windows.Forms.FolderBrowserDialog;" ^
 "$d.Description='Selecione a pasta de destino. Selecione Program Files para usar o padrao KrotonIA, ou escolha/crie outra pasta.';" ^
 "$d.ShowNewFolderButton=$true; $d.SelectedPath=$env:ProgramFiles;" ^
 "if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){[System.IO.File]::WriteAllText($env:KROTON_PICKER_FILE,$d.SelectedPath)}"
if not exist "!PICKER_FILE!" (
    echo.
    echo [!] Selecao de pasta cancelada.
    exit /b 1
)
set /p "PICKED_PATH="<"!PICKER_FILE!"
del /q "!PICKER_FILE!"
if not defined PICKED_PATH exit /b 1
if /I "!PICKED_PATH!"=="%ProgramFiles%" (
    set "INSTALL_PATH=%ProgramFiles%\KrotonIA"
) else (
    set "INSTALL_PATH=!PICKED_PATH!"
)
exit /b 0

:InstallCore
cls
echo.
echo [1/5] Criando diretorios...
if not exist "!INSTALL_PATH!" mkdir "!INSTALL_PATH!"
if not exist "!INSTALL_PATH!\runtime" mkdir "!INSTALL_PATH!\runtime"
if not exist "!INSTALL_PATH!\backup" mkdir "!INSTALL_PATH!\backup"
if not exist "!INSTALL_PATH!\tools" mkdir "!INSTALL_PATH!\tools"
if not exist "!INSTALL_PATH!\app" mkdir "!INSTALL_PATH!\app"

echo [2/5] Copiando arquivos...
for %%F in (
    Install-Kroton.bat
    Install-Kroton.ps1
    Install-KrotonService.ps1
    Install-KrotonStartup.ps1
    KrotonIA.exe
    KrotonGameModeRunner.ps1
    Optimize-BackgroundServices.ps1
    game-mode-config.json
    service-profile.json
    service-profile.game.json
    service-profile.light.json
    service-profile.normal.json
    Uninstall-Kroton.bat
    Uninstall-Kroton.ps1
    Uninstall-KrotonService.ps1
    Uninstall-KrotonStartup.ps1
) do (
    set "SRC_FILE=!SOURCE_DIR!\%%F"
    if not exist "!SRC_FILE!" set "SRC_FILE=!SCRIPT_DIR!\%%F"
    if not exist "!SRC_FILE!" (
        echo [ERRO] Arquivo ausente: %%F
        exit /b 1
    )
    if /I "!SRC_FILE!"=="!INSTALL_PATH!\%%F" (
        echo [SKIP] %%F ja esta no destino
    ) else (
        copy /Y "!SRC_FILE!" "!INSTALL_PATH!\" >nul
        if errorlevel 1 (
            echo [ERRO] Falha ao copiar: %%F
            exit /b 1
        )
    )
)
copy /Y "!SOURCE_DIR!\app\List-CatalogServices.ps1" "!INSTALL_PATH!\app\" >nul
if errorlevel 1 (
    echo [ERRO] Falha ao copiar: app\List-CatalogServices.ps1
    exit /b 1
)

echo [3/5] Registrando no Painel de Controle...
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "DisplayName" /t REG_SZ /d "Kroton.ia - Game Mode Optimizer" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "DisplayVersion" /t REG_SZ /d "1.2" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "InstallLocation" /t REG_SZ /d "!INSTALL_PATH!" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "UninstallString" /t REG_SZ /d "powershell.exe -ExecutionPolicy Bypass -File '!INSTALL_PATH!\Uninstall-Kroton.ps1'" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "Publisher" /t REG_SZ /d "Kroton.ia" /f >nul
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA" /v "DisplayIcon" /t REG_SZ /d "!INSTALL_PATH!\KrotonIA.exe,0" /f >nul
if errorlevel 1 (
    echo [ERRO] Falha ao registrar no Painel de Controle.
    exit /b 1
)

echo [4/5] Criando atalho na Area de Trabalho...
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Kroton.ia.lnk'); $s.TargetPath='!INSTALL_PATH!\KrotonIA.exe'; $s.WorkingDirectory='!INSTALL_PATH!'; $s.Save()"

echo [5/5] Base instalada com sucesso.
exit /b 0

:RegisterSystemTask
echo.
echo Registrando tarefa agendada SYSTEM...
PowerShell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$taskName='!TASK_NAME!';" ^
 "$runnerPath='!INSTALL_PATH!\KrotonGameModeRunner.ps1';" ^
 "$task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue;" ^
 "if ($task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -Force };" ^
 "$action=New-ScheduledTaskAction -Execute 'PowerShell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""' + $runnerPath + '""');" ^
 "$trigger=New-ScheduledTaskTrigger -AtLogOn;" ^
 "$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest;" ^
 "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" ^
 "Register-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -TaskName $taskName -Description 'Kroton.ia - Game Mode Auto-Switch' -Force | Out-Null;" ^
 "Start-ScheduledTask -TaskName $taskName"
exit /b %ERRORLEVEL%

:RegisterStartupTask
echo.
echo Registrando tarefa por usuario...
PowerShell -NoProfile -ExecutionPolicy Bypass -File "!INSTALL_PATH!\Install-KrotonStartup.ps1" -RunnerPath "!INSTALL_PATH!\KrotonGameModeRunner.ps1"
exit /b %ERRORLEVEL%

:RegisterWindowsService
echo.
echo Registrando servico Windows...
PowerShell -NoProfile -ExecutionPolicy Bypass -File "!INSTALL_PATH!\Install-KrotonService.ps1" -RunnerPath "!INSTALL_PATH!\KrotonGameModeRunner.ps1"
exit /b %ERRORLEVEL%

:RemoveServiceIfPresent
if not exist "!INSTALL_PATH!\Uninstall-KrotonService.ps1" exit /b 0
PowerShell -NoProfile -ExecutionPolicy Bypass -File "!INSTALL_PATH!\Uninstall-KrotonService.ps1" >nul 2>&1
exit /b 0

:ResolveWorkDir
set "WORK_DIR=!SOURCE_DIR!"
if exist "!INSTALL_PATH!\Optimize-BackgroundServices.ps1" set "WORK_DIR=!INSTALL_PATH!"
exit /b 0

:ValidateSourceFiles
set "MISSING_FILE="
for %%F in (
    Install-Kroton.ps1
    Install-KrotonService.ps1
    Install-KrotonStartup.ps1
    KrotonGameModeRunner.ps1
    Optimize-BackgroundServices.ps1
    Uninstall-Kroton.ps1
    Uninstall-KrotonService.ps1
    Uninstall-KrotonStartup.ps1
    game-mode-config.json
    service-profile.json
    service-profile.game.json
    service-profile.light.json
    service-profile.normal.json
) do (
    if not exist "!SCRIPT_DIR!\%%F" if not exist "!SOURCE_DIR!\%%F" (
        echo [ERRO] Arquivo ausente: %%F
        set "MISSING_FILE=1"
    )
)
if defined MISSING_FILE (
    echo.
    pause
    exit /b 1
)
exit /b 0

:OPERATION_SUCCESS
echo.
echo =====================================
echo   OPERACAO CONCLUIDA
echo =====================================
echo.
echo [OK] !SUCCESS_MESSAGE!
echo.
pause
goto MENU

:OPERATION_FAILED
echo.
echo [ERRO] A operacao falhou.
echo.
pause
goto MENU