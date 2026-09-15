@echo off
REM Prepare l'arborescence attendue par l'installeur NSIS.
REM Publication autonome : aucun runtime .NET a installer sur le serveur.

setlocal
cd /d "%~dp0.."

echo Publication du service...
dotnet publish src\ACPoller.Service\ACPoller.Service.csproj -c Release -o installer\service || exit /b 1

echo Publication de l'interface...
dotnet publish src\ACPoller.Ui\ACPoller.Ui.csproj -c Release -o installer\ui || exit /b 1

echo Copie des scripts...
copy /y installer\install-service.ps1 installer\service\ >nul
copy /y installer\uninstall-service.ps1 installer\service\ >nul

echo.
echo Prochaine etape : makensis installer\ACPoller.nsi
