@echo off
REM BioSynth : lancement sous Windows (double-clic).
REM Compile en Release puis ouvre la fenetre BioSynth. Fermer la fenetre arrete BioSynth.
chcp 65001 >nul
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le SDK .NET 8 est introuvable : https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
dotnet build BioSynth\BioSynth.csproj -c Release -v quiet -nologo
if errorlevel 1 (
  pause
  exit /b 1
)
dotnet BioSynth\bin\Release\net8.0\BioSynth.dll %*
