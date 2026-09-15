@echo off
REM Regeneration des documents PDF depuis les sources markdown.
REM
REM Prerequis : pandoc, wkhtmltopdf, Python avec pypdf, reportlab et Pillow.
REM   choco install pandoc wkhtmltopdf
REM   pip install pypdf reportlab pillow
REM
REM Les couvertures sont des images deja generees : les regenerer n'est utile
REM que si la charte change. Le script build-couvertures.py s'en charge.

setlocal
cd /d "%~dp0"

echo Generation des documents francais...
python generer.py fr || exit /b 1

echo Generation des documents anglais...
python generer.py en || exit /b 1

echo.
echo Documents generes dans docs\fr et docs\en
