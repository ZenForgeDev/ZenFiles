@echo off
setlocal
cd /d "%~dp0"
rem Laster opp kildekoden til GitHub og lager release for versjonen i version.txt.
rem Kjør build.bat (valg 2) først, så installer\ZFSetup.exe finnes.

set /p VERSION=<version.txt
echo ==== Release ZenFiles v%VERSION% ====
echo.
where git >nul 2>nul || (echo FEIL: git ble ikke funnet. & goto :fail)

git status --short
echo.
choice /c JN /m "Committe alt over og pushe til GitHub"
if errorlevel 2 goto :end

git add -A
git commit -m "ZenFiles %VERSION%"
git push
if errorlevel 1 goto :fail

git tag -a v%VERSION% -m "ZenFiles %VERSION%"
git push origin v%VERSION%
if errorlevel 1 goto :fail

where gh >nul 2>nul
if errorlevel 1 (
    echo.
    echo gh ^(GitHub CLI^) mangler. Kildekoden og taggen er pushet.
    echo Lag release manuelt: github.com/ZenForgeDev/ZenFiles/releases/new ^(velg tag v%VERSION%^)
    echo og last opp installer\ZFSetup.exe.
    goto :end
)
if not exist installer\ZFSetup.exe (
    echo FEIL: installer\ZFSetup.exe finnes ikke. Kjor build.bat valg 2 forst.
    goto :fail
)
copy /y installer\ZFSetup.exe "installer\ZenFiles-Setup-%VERSION%.exe" >nul
gh release create v%VERSION% "installer\ZenFiles-Setup-%VERSION%.exe" --title "ZenFiles %VERSION%" --generate-notes --prerelease
if errorlevel 1 goto :fail
echo.
echo FERDIG: release v%VERSION% er publisert.
goto :end

:fail
echo.
echo Feilet. Se meldingene over.
pause
exit /b 1

:end
endlocal
