@echo off
REM Refuse l'indexation de ce qui ne doit jamais partir sur un depot distant.
REM
REM Un fichier pousse reste dans l'historique meme apres suppression : la seule
REM sortie est une reecriture d'historique, avec tous les clones a refaire.
REM Autant s'arreter avant.
REM
REM Code de retour 1 si quelque chose de sensible est indexe.

setlocal enabledelayedexpansion
set FAUTIF=0

git diff --cached --name-only > "%TEMP%\acp-index.txt" 2>nul

REM appsettings.json, sauf le modele neutre
findstr /i /c:"appsettings" "%TEMP%\acp-index.txt" | findstr /v /i "sample" > "%TEMP%\acp-hit.txt"
for /f %%f in ("%TEMP%\acp-hit.txt") do if %%~zf gtr 0 (
  echo.
  echo ERREUR : un fichier de configuration est indexe.
  type "%TEMP%\acp-hit.txt"
  echo Il porte les identifiants de boites, les secrets clients et le jeton.
  echo Retirer avec : git reset ^<chemin^>
  set FAUTIF=1
)

REM Reglages d'interface : ils portent le jeton de pilotage en clair
findstr /i /c:"ui-settings" "%TEMP%\acp-index.txt" > "%TEMP%\acp-hit.txt"
for /f %%f in ("%TEMP%\acp-hit.txt") do if %%~zf gtr 0 (
  echo.
  echo ERREUR : un fichier ui-settings est indexe.
  type "%TEMP%\acp-hit.txt"
  echo Il porte le jeton de pilotage en clair.
  set FAUTIF=1
)

REM Sauvegardes : les .clear.*.bak portent les secrets NON chiffres
findstr /i /c:".bak" "%TEMP%\acp-index.txt" > "%TEMP%\acp-hit.txt"
for /f %%f in ("%TEMP%\acp-hit.txt") do if %%~zf gtr 0 (
  echo.
  echo ERREUR : une sauvegarde est indexee.
  type "%TEMP%\acp-hit.txt"
  echo Les fichiers .clear.*.bak portent les secrets EN CLAIR.
  set FAUTIF=1
)

REM Binaires tiers : LibreOffice.msi depasse la limite de 100 Mo de GitHub
findstr /i /c:"installer/prereq/" "%TEMP%\acp-index.txt" > "%TEMP%\acp-hit.txt"
for /f %%f in ("%TEMP%\acp-hit.txt") do if %%~zf gtr 0 (
  echo.
  echo ERREUR : installer/prereq est indexe.
  echo LibreOffice.msi fait 366 Mo, au-dela de la limite de GitHub.
  set FAUTIF=1
)

REM Certificats et cles privees
findstr /i /r /c:"\.pfx$" /c:"\.p12$" /c:"\.pem$" /c:"\.key$" "%TEMP%\acp-index.txt" > "%TEMP%\acp-hit.txt"
for /f %%f in ("%TEMP%\acp-hit.txt") do if %%~zf gtr 0 (
  echo.
  echo ERREUR : un certificat ou une cle privee est indexe.
  type "%TEMP%\acp-hit.txt"
  set FAUTIF=1
)

del /q "%TEMP%\acp-index.txt" "%TEMP%\acp-hit.txt" 2>nul

if "%FAUTIF%"=="1" (
  echo.
  exit /b 1
)

echo Index verifie : rien de sensible.
exit /b 0
