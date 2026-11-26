@echo off
rem ── Corn Downloader release build ───────────────────────────────────────────
rem  1. dotnet publish  -> self-contained single-file CornDownloader.exe
rem  2. Inno Setup 6    -> CornDownloader-Setup-<version>.exe
rem  3. Copies both into .\output\ as the two release assets + SHA256SUMS.txt
rem  Everything is also written to output\build-release.log.
setlocal EnableExtensions
set "ROOT=%~dp0"
if not exist "%ROOT%output" mkdir "%ROOT%output"
set "LOG=%ROOT%output\build-release.log"
echo Building Corn Downloader... (log: %LOG%)
call :main > "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
type "%LOG%"
if "%RC%"=="0" (echo. & echo === BUILD OK ===) else (echo. & echo === BUILD FAILED [%RC%] ===)
timeout /t 30
exit /b %RC%

:main
cd /d "%ROOT%CornDownloader" || exit /b 2
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'CornDownloader.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "VER=%%v"
if not defined VER (echo Could not read ^<Version^> from CornDownloader.csproj & exit /b 3)
echo === Corn Downloader %VER% — %DATE% %TIME% ===
echo.
echo === [1/3] dotnet publish ===
dotnet --version
dotnet publish -c Release -nologo || exit /b 10
set "PUB=bin\Release\net10.0-windows\win-x64\publish\CornDownloader.exe"
if not exist "%PUB%" (echo Publish output missing: %PUB% & exit /b 11)

echo.
echo === [2/3] Inno Setup ===
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC for /f "delims=" %%i in ('where iscc 2^>nul') do set "ISCC=%%i"
if not defined ISCC (echo Inno Setup 6 [ISCC.exe] not found & exit /b 20)
echo Using "%ISCC%"
"%ISCC%" /Q CornDownloader.iss || exit /b 21
set "SETUP=installer_output\CornDownloader-Setup-%VER%.exe"
if not exist "%SETUP%" (echo Installer output missing: %SETUP% & exit /b 22)

echo.
echo === [3/3] Collect release assets ===
copy /y "%PUB%"   "%ROOT%output\CornDownloader-%VER%-portable.exe" || exit /b 30
copy /y "%SETUP%" "%ROOT%output\CornDownloader-Setup-%VER%.exe"   || exit /b 31
powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 '%ROOT%output\CornDownloader-Setup-%VER%.exe','%ROOT%output\CornDownloader-%VER%-portable.exe' | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf) } | Set-Content -Encoding ascii '%ROOT%output\SHA256SUMS.txt'; Get-Content '%ROOT%output\SHA256SUMS.txt'"
powershell -NoProfile -Command "Get-Item '%ROOT%output\CornDownloader-*%VER%*.exe' | ForEach-Object { '{0}  {1:N1} MB  v{2}' -f $_.Name, ($_.Length/1MB), $_.VersionInfo.ProductVersion }"
exit /b 0
