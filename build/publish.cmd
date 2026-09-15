@echo off
REM Publication autonome : aucun runtime .NET a installer sur le serveur client.
REM Prerequis serveur : Windows Server 2016 minimum (.NET 10).
setlocal
cd /d "%~dp0.."
dotnet publish src\ACPoller.Service\ACPoller.Service.csproj -c Release -o artifacts\service || exit /b 1
dotnet publish src\ACPoller.Ui\ACPoller.Ui.csproj -c Release -o artifacts\ui || exit /b 1
echo.
echo Publie dans artifacts\
