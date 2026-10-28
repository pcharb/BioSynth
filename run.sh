#!/usr/bin/env bash
# BioSynth : lancement sous Linux ou macOS.
# Compile en Release puis ouvre la fenêtre BioSynth. Fermer la fenêtre arrête l'application.
# Options : --port N, --browser (onglet du navigateur par défaut), --no-browser, --keep-server, --lan.
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Le SDK .NET 8 est introuvable."
  echo "  Ubuntu 22.04+ / Debian : sudo apt install dotnet-sdk-8.0"
  echo "  Fedora                 : sudo dnf install dotnet-sdk-8.0"
  echo "  macOS                  : brew install dotnet@8"
  echo "  Autres                 : https://dotnet.microsoft.com/download/dotnet/8.0"
  exit 1
fi

# grep sans -q : avec pipefail, -q fermerait le tube tôt et ldconfig échouerait (SIGPIPE)
if [ "$(uname)" = "Linux" ] && ! ldconfig -p 2>/dev/null | grep "liblsl.so" >/dev/null; then
  echo "Note : liblsl introuvable, les flux LSL seront désactivés (le reste fonctionne)."
  echo "       Voir README_LSL.txt."
fi

dotnet build BioSynth/BioSynth.csproj -c Release -v quiet -nologo
exec dotnet BioSynth/bin/Release/net8.0/BioSynth.dll "$@"
