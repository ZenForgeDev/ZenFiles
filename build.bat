@echo off
setlocal
for /f "tokens=2 delims=:" %%a in ('chcp') do set "OLDCP=%%a"
if not defined OLDCP set "OLDCP= 850"
set "OLDCP=%OLDCP: =%"
chcp 65001 >nul
cd /d "%~dp0"

echo ==== ZenFiles build ====
echo.
echo  Hva vil du bygge?
echo.
echo  [1] Bygg appen (kjørbar mappe)
echo      Lager publish\ZenFiles.exe med alle filene den trenger ved siden av.
echo      Ingenting blir installert - du kan starte appen rett fra mappen.
echo      Best når du tester og bygger ofte.
echo.
echo  [2] Bygg installer (ZFSetup.exe)
echo      Bygger appen som i valg 1, og pakker alt i en setup-fil:
echo      installer\ZFSetup.exe. Den installerer ZenFiles som et vanlig
echo      program (startmeny, valgfri skrivebordssnarvei, avinstallering
echo      i Innstillinger) og kan deles med andre.
echo      Krever Inno Setup - tilbyr å installere det hvis det mangler.
echo.
choice /c 12 /n /m "Velg 1 eller 2: "
set "MODE=%errorlevel%"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo FEIL: dotnet ble ikke funnet. Installer .NET 8 SDK: https://dotnet.microsoft.com/download
    goto :fail
)

echo.
echo [1/2] Gjenoppretter pakker...
dotnet restore ZenFiles.csproj -p:Platform=x64
if errorlevel 1 goto :fail

echo.
echo [2/2] Publiserer (Release, win-x64, self-contained)...
dotnet publish ZenFiles.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o "%~dp0publish"
if errorlevel 1 goto :fail

rem Terminalkontrollen leter etter TerminalControl.xaml i appens rot, men pakken legger den i en undermappe.
if exist "%~dp0publish\Microsoft.Terminal.WinUI3\TerminalControl.xbf" (
    copy /y "%~dp0publish\Microsoft.Terminal.WinUI3\TerminalControl.*" "%~dp0publish\" >nul
)

if "%MODE%"=="2" goto :installer

echo.
echo FERDIG: %~dp0publish\ZenFiles.exe
echo (Bruk denne kopien hvis du slår på "Standard filbehandler" - ikke bin\Debug.)
echo.
choice /c JN /m "Starte ZenFiles nå"
if errorlevel 2 goto :end
start "" "%~dp0publish\ZenFiles.exe"
goto :end

:installer
echo.
echo [3/3] Lager installer (Inno Setup)...
call :findiscc
if defined ISCC goto :runiscc

echo.
echo Inno Setup ble ikke funnet. Det trengs for å lage ZFSetup.exe.
where winget >nul 2>nul
if errorlevel 1 goto :noinno
choice /c JN /m "Installere Inno Setup med winget nå"
if errorlevel 2 goto :noinno
winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
call :findiscc
if defined ISCC goto :runiscc

:noinno
echo.
echo Installer Inno Setup fra https://jrsoftware.org/isdl.php og kjør build.bat på nytt.
goto :fail

:runiscc
"%ISCC%" "%~dp0Installer\ZenFiles.iss"
if errorlevel 1 goto :fail
echo.
echo FERDIG: %~dp0installer\ZFSetup.exe
echo.
choice /c JN /m "Starte installeren nå"
if errorlevel 2 goto :end
start "" "%~dp0installer\ZFSetup.exe"
goto :end

:findiscc
set "ISCC="
for /f "delims=" %%I in ('where iscc 2^>nul') do set "ISCC=%%I"
if defined ISCC exit /b 0
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
exit /b 0

:fail
echo.
echo Bygget feilet. Se feilmeldingene over.
pause
chcp %OLDCP% >nul
exit /b 1

:end
chcp %OLDCP% >nul
endlocal
