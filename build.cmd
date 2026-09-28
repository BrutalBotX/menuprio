@echo off
setlocal

set "PF86=%ProgramFiles(x86)%"
set "PF=%ProgramFiles%"

rem --- locate Roslyn csc.exe (ships with Visual Studio / Build Tools) ---
set "CSC="
for %%P in (
  "%PF86%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF86%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF86%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF86%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
  "%PF%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
) do (
  if not defined CSC if exist %%P set "CSC=%%~P"
)

if not defined CSC (
  echo ERROR: could not find Roslyn csc.exe.
  echo Install "Visual Studio Build Tools" with the .NET desktop build tools workload.
  exit /b 1
)

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\System.Windows.Forms.dll" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\System.Windows.Forms.dll" (
  echo ERROR: .NET Framework 4.x not found.
  exit /b 1
)

echo Compiler : %CSC%
echo Framework: %FW%
echo.

set "ICON="
if exist "assets\menuprio.ico" set "ICON=/win32icon:assets\menuprio.ico"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ %ICON% /out:MenuPrio.exe ^
  /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" ^
  src\*.cs
if errorlevel 1 ( echo. & echo BUILD FAILED & exit /b 1 )

"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ %ICON% /out:MenuPrioTest.exe ^
  /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" ^
  src\*.cs
if errorlevel 1 ( echo. & echo TEST BUILD FAILED & exit /b 1 )

echo.
echo Build OK: MenuPrio.exe (tray app) and MenuPrioTest.exe (self-test)
