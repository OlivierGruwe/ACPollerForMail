<#
.SYNOPSIS
    Desinstalle le service ACPollerForMail.

.DESCRIPTION
    Les DONNEES sont conservees par defaut : repertoire de travail, journal des
    messages traites, quarantaine. Une desinstallation qui efface le journal
    des traitements provoquerait un retraitement complet a la reinstallation,
    donc des doublons en GED. C'est exactement le genre de nettoyage bien
    intentionne qui coute une journee de reconciliation.

.PARAMETER DataPath
    Repertoire de donnees.

.PARAMETER RemoveData
    Supprimer aussi les donnees. A n'utiliser que pour une desinstallation
    definitive.
#>
[CmdletBinding()]
param(
    [string] $DataPath = "$env:ProgramData\ACPollerForMail",
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'ACPollerForMail'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "  Arret du service" -ForegroundColor Cyan
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', '00:01:00')
    }

    Write-Host "  Suppression du service" -ForegroundColor Cyan
    & sc.exe delete $serviceName | Out-Null
} else {
    Write-Host "  Service deja absent" -ForegroundColor Yellow
}

# Les processus LibreOffice orphelins survivent a l'arret du service : ils
# gardent leur profil verrouille et empechent une reinstallation propre.
Get-Process -Name 'soffice', 'soffice.bin' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "*$DataPath*" -or $_.Path -like '*LibreOffice*' } |
    ForEach-Object {
        Write-Host "  Arret d'un processus LibreOffice orphelin (PID $($_.Id))" -ForegroundColor Cyan
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

if ($RemoveData) {
    Write-Warning "Suppression des donnees : $DataPath"
    Write-Warning "Le journal des messages traites sera perdu. Une reinstallation retraitera l'historique present dans les boites."

    if (Test-Path $DataPath) {
        Remove-Item -Path $DataPath -Recurse -Force
    }
} else {
    Write-Host ""
    Write-Host "Donnees conservees dans $DataPath" -ForegroundColor Green
    Write-Host "Relancer avec -RemoveData pour les supprimer." -ForegroundColor Gray
}

Write-Host ""
Write-Host "Desinstallation terminee." -ForegroundColor Green
