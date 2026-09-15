<#
.SYNOPSIS
    Renseigne les chemins de LibreOffice et qpdf dans la configuration.

.DESCRIPTION
    Appele par l'installeur apres l'installation des outils optionnels.

    N'ecrit QUE si la valeur est absente ou pointe vers un fichier inexistant :
    un exploitant qui a deliberement configure une autre installation de
    LibreOffice ne doit pas voir son choix ecrase a chaque mise a jour.

.PARAMETER SettingsPath
    Chemin du fichier de configuration.

.PARAMETER LibreOfficePath
    Chemin de soffice.exe. Detecte automatiquement si vide.

.PARAMETER QpdfPath
    Chemin de qpdf.exe. Detecte automatiquement si vide.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SettingsPath,

    [string] $LibreOfficePath = "",
    [string] $QpdfPath = ""
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SettingsPath)) {
    Write-Warning "Configuration introuvable : $SettingsPath"
    exit 0
}

function Find-Tool([string[]] $candidates) {
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

# Ordre de recherche : ce qui est fourni, puis l'installation locale au produit,
# puis les emplacements standard. L'installation locale prime sur celle du
# systeme : c'est celle dont on maitrise la version.
$installDir = Split-Path -Parent $SettingsPath

$libreOffice = Find-Tool @(
    $LibreOfficePath,
    (Join-Path $installDir 'libreoffice\program\soffice.exe'),
    "$env:ProgramFiles\LibreOffice\program\soffice.exe",
    "${env:ProgramFiles(x86)}\LibreOffice\program\soffice.exe"
)

$qpdf = Find-Tool @(
    $QpdfPath,
    (Join-Path $installDir 'qpdf\bin\qpdf.exe'),
    (Join-Path $installDir 'qpdf\qpdf.exe'),
    "$env:ProgramFiles\qpdf\bin\qpdf.exe"
)

$json = Get-Content $SettingsPath -Raw | ConvertFrom-Json

if (-not $json.Conversion) {
    $json | Add-Member -NotePropertyName Conversion -NotePropertyValue ([PSCustomObject]@{}) -Force
}

if (-not $json.Conversion.LibreOffice) {
    $json.Conversion | Add-Member -NotePropertyName LibreOffice `
        -NotePropertyValue ([PSCustomObject]@{}) -Force
}

if (-not $json.Conversion.Pdf) {
    $json.Conversion | Add-Member -NotePropertyName Pdf `
        -NotePropertyValue ([PSCustomObject]@{}) -Force
}

$changed = $false

# Le chemin configure n'est remplace que s'il est vide ou pointe dans le vide :
# un choix delibere de l'exploitant est respecte.
$currentLo = $json.Conversion.LibreOffice.SofficePath

if ($libreOffice -and (-not $currentLo -or -not (Test-Path $currentLo))) {
    $json.Conversion.LibreOffice | Add-Member -NotePropertyName SofficePath `
        -NotePropertyValue $libreOffice -Force
    Write-Host "  LibreOffice : $libreOffice" -ForegroundColor Green
    $changed = $true
}

$currentQpdf = $json.Conversion.Pdf.QpdfPath

if ($qpdf -and (-not $currentQpdf -or -not (Test-Path $currentQpdf))) {
    $json.Conversion.Pdf | Add-Member -NotePropertyName QpdfPath `
        -NotePropertyValue $qpdf -Force
    Write-Host "  qpdf : $qpdf" -ForegroundColor Green
    $changed = $true
}

if (-not $changed) {
    Write-Host "  Chemins des outils inchanges." -ForegroundColor Gray
    exit 0
}

# Sauvegarde avant reecriture : ce fichier porte le parametrage de toutes les
# boites, une erreur de serialisation ne doit pas le detruire.
$backup = "$SettingsPath.$(Get-Date -Format 'yyyyMMddHHmmss').bak"
Copy-Item $SettingsPath $backup

$json | ConvertTo-Json -Depth 32 | Set-Content $SettingsPath -Encoding UTF8

Write-Host "  Configuration mise a jour (sauvegarde : $backup)" -ForegroundColor Green
