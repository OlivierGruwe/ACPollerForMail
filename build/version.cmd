@echo off
REM Bump de la version unique de la solution.
REM Usage : version.cmd 2.1.0
if "%~1"=="" (
  echo Usage: version.cmd ^<x.y.z^>
  exit /b 1
)
powershell -NoProfile -Command ^
  "(Get-Content ..\Directory.Build.props) -replace '<VersionPrefix>.*</VersionPrefix>', '<VersionPrefix>%~1</VersionPrefix>' | Set-Content ..\Directory.Build.props"
echo Version portee a %~1
