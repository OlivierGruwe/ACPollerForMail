<#
.SYNOPSIS
    Installe le service ACPoller : repertoires, droits, jeton, service Windows.

.DESCRIPTION
    Appele par l'installeur, ou a la main pour une installation controlee.
    Idempotent : rejouable sans effet de bord sur une installation existante.

    Quatre choses que ce script fait et qu'on oublie systematiquement en
    installation manuelle :

    1. Le JETON de pilotage, genere et propage dans les deux fichiers qui en
       ont besoin. Sans cela, l'exploitant doit le recopier a la main de la
       configuration du service vers celle de l'interface, sur chaque poste.

    2. Les DROITS NTFS sur appsettings.json. Le chiffrement DPAPI protege
       contre l'exfiltration du fichier, pas contre sa lecture par un
       utilisateur local.

    3. Le COMPTE de service. Le compte systeme local n'a AUCUNE identite
       reseau : ni partage UNC, ni FTP interne. Le piege ne se manifeste qu'au
       premier export.

    4. Le REDEMARRAGE automatique apres echec. Un service arrete la nuit que
       personne ne relance avant lundi, c'est trois jours de factures non
       integrees.

.PARAMETER InstallPath
    Repertoire d'installation des binaires.

.PARAMETER DataPath
    Repertoire de travail et de donnees.

.PARAMETER ServiceAccount
    Compte de service. Vide pour LocalSystem, deconseille en production.

.PARAMETER ServicePassword
    Mot de passe du compte de service.

.PARAMETER ResetToken
    Regenere le jeton de pilotage meme s'il en existe deja un. A utiliser
    quand le fichier de reglages de l'interface a ete perdu : le jeton du
    service etant chiffre, il n'est pas recuperable en clair.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = "$env:ProgramFiles\ACPoller",
    [string] $DataPath = "$env:ProgramData\ACPoller",
    [string] $ServiceAccount = "",
    [string] $ServicePassword = "",
    [switch] $ResetToken
)

$ErrorActionPreference = 'Stop'
$serviceName = 'ACPoller'
$exePath = Join-Path $InstallPath 'ACPoller.Service.exe'
$settingsPath = Join-Path $InstallPath 'appsettings.json'
$uiDefaultsPath = Join-Path $DataPath 'ui-settings.default.json'

function Write-Step($message) {
    Write-Host "  $message" -ForegroundColor Cyan
}

function New-ControlToken {
    # Generateur cryptographique et non Get-Random : ce jeton donne le
    # pilotage complet du service. Hexadecimal pour eviter tout probleme
    # d'echappement dans un en-tete HTTP ou un fichier de configuration.
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)

    return ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

Write-Host "Installation d'ACPoller" -ForegroundColor White

# --- Verifications prealables -------------------------------------------------

if (-not (Test-Path $exePath)) {
    throw "Executable introuvable : $exePath. Deployer les binaires avant d'installer le service."
}

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Ce script demande des droits administrateur."
}

# --- Repertoires de donnees ---------------------------------------------------

Write-Step "Creation des repertoires sous $DataPath"

$directories = @(
    $DataPath,
    (Join-Path $DataPath 'work'),
    (Join-Path $DataPath 'work\state'),
    (Join-Path $DataPath 'lo-profiles'),
    (Join-Path $DataPath 'plugins'),
    (Join-Path $DataPath 'stylesheets')
)

foreach ($directory in $directories) {
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

# --- Jeton de pilotage --------------------------------------------------------
#
# Le jeton est genere UNE FOIS et propage dans les deux fichiers. Sur une mise
# a jour, il est conserve : le regenerer invaliderait les reglages de tous les
# postes d'exploitation deja configures.

if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

    if (-not $settings.ControlApi) {
        $settings | Add-Member -NotePropertyName ControlApi -NotePropertyValue ([PSCustomObject]@{
            Enabled = $true
            Port    = 5199
            Token   = ""
        }) -Force
    }

    $currentToken = $settings.ControlApi.Token
    $isEncrypted = $currentToken -and $currentToken.StartsWith('ENC:')

    if ($ResetToken -or -not $currentToken) {
        $token = New-ControlToken

        $settings.ControlApi | Add-Member -NotePropertyName Token -NotePropertyValue $token -Force
        $settings | ConvertTo-Json -Depth 32 | Set-Content $settingsPath -Encoding UTF8

        Write-Step "Jeton de pilotage genere"

        # Reglages par defaut de l'interface, lisibles par tous les
        # exploitants du serveur. L'interface les utilise au premier
        # lancement, puis les reglages propres a l'utilisateur priment.
        $port = if ($settings.ControlApi.Port) { $settings.ControlApi.Port } else { 5199 }

        @{
            BaseAddress = "http://127.0.0.1:$port"
            Token       = $token
            Theme       = 0
        } | ConvertTo-Json | Set-Content $uiDefaultsPath -Encoding UTF8

        Write-Step "Reglages d'interface deposes : $uiDefaultsPath"
    }
    elseif ($isEncrypted -and -not (Test-Path $uiDefaultsPath)) {
        # Cas a signaler : le service a chiffre son jeton, il n'est donc plus
        # recuperable en clair, et le fichier de l'interface a disparu. La
        # seule issue est de regenerer les deux.
        Write-Warning @"
Le jeton du service est chiffre et les reglages d'interface sont absents.
L'interface ne pourra pas se connecter. Relancer ce script avec -ResetToken
pour regenerer le jeton et redeployer les reglages.
"@
    }
    else {
        Write-Step "Jeton de pilotage existant conserve"
    }
}

# --- Compte de service --------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
    $account = 'LocalSystem'

    Write-Warning @"
Aucun compte de service fourni : le service tournera en LocalSystem.
Ce compte n'a AUCUNE identite reseau. Les exports vers un partage UNC et les
connexions FTP internes echoueront, et l'erreur n'apparaitra qu'au premier
message traite. Prevoir un compte de domaine dedie.
"@
} else {
    $account = $ServiceAccount
    Write-Step "Compte de service : $account"
}

# --- Droits -------------------------------------------------------------------

Write-Step "Attribution des droits sur $DataPath"

if ($account -ne 'LocalSystem') {
    # Modification et non Controle total : le service n'a aucune raison de
    # pouvoir changer les droits de ses propres repertoires.
    & icacls.exe $DataPath /grant "${account}:(OI)(CI)M" /T /Q | Out-Null
}

if (Test-Path $settingsPath) {
    Write-Step "Restriction des droits sur appsettings.json"

    # Heritage rompu, puis liste blanche. Le fichier contient les secrets
    # chiffres : DPAPI en portee machine ne protege pas contre un processus
    # local, c'est cette ACL qui complete la protection.
    & icacls.exe $settingsPath /inheritance:r /Q | Out-Null
    & icacls.exe $settingsPath /grant "*S-1-5-32-544:(F)" /Q | Out-Null   # Administrateurs
    & icacls.exe $settingsPath /grant "*S-1-5-18:(F)" /Q | Out-Null       # SYSTEM

    if ($account -ne 'LocalSystem') {
        & icacls.exe $settingsPath /grant "${account}:(M)" /Q | Out-Null
    }
}

if (Test-Path $uiDefaultsPath) {
    Write-Step "Restriction des droits sur les reglages d'interface"

    # Le jeton y est EN CLAIR : il donne acces au pilotage du service, a la
    # relance des workers et a la lecture de la configuration, secrets exclus.
    # Lecture reservee aux administrateurs, qui sont les seuls a exploiter le
    # service. Ajouter un groupe d'exploitation ici si le besoin existe.
    & icacls.exe $uiDefaultsPath /inheritance:r /Q | Out-Null
    & icacls.exe $uiDefaultsPath /grant "*S-1-5-32-544:(R)" /Q | Out-Null  # Administrateurs, lecture
    & icacls.exe $uiDefaultsPath /grant "*S-1-5-18:(F)" /Q | Out-Null      # SYSTEM
}

# --- Service Windows ----------------------------------------------------------

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Step "Service existant : arret et mise a jour"

    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', '00:01:00')
    }

    & sc.exe config $serviceName binPath= "`"$exePath`"" | Out-Null
} else {
    Write-Step "Creation du service"

    $arguments = @(
        'create', $serviceName,
        'binPath=', "`"$exePath`"",
        'start=', 'auto',
        'DisplayName=', 'ACPoller - Capture de messagerie'
    )

    if ($account -ne 'LocalSystem') {
        $arguments += @('obj=', $account, 'password=', $ServicePassword)
    }

    & sc.exe @arguments | Out-Null
}

& sc.exe description $serviceName `
    "Capture des messages, conversion PDF et depot en GED." | Out-Null

# Redemarrage automatique : trois tentatives espacees, puis remise a zero du
# compteur au bout d'une journee.
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# Demarrage differe : LibreOffice, le reseau et les partages ne sont pas
# forcement disponibles au tout debut du demarrage de Windows.
& sc.exe config $serviceName start= delayed-auto | Out-Null

# --- Prerequis ----------------------------------------------------------------

Write-Host ""
Write-Host "Verification des prerequis" -ForegroundColor White

$libreOffice = "$env:ProgramFiles\LibreOffice\program\soffice.exe"
$qpdf = Join-Path $InstallPath 'qpdf\bin\qpdf.exe'

if (Test-Path $libreOffice) {
    Write-Host "  LibreOffice : $libreOffice" -ForegroundColor Green
} else {
    Write-Warning "LibreOffice introuvable. Les pieces bureautiques ne seront pas converties."
}

if (Test-Path $qpdf) {
    Write-Host "  qpdf : $qpdf" -ForegroundColor Green
} else {
    Write-Warning @"
qpdf introuvable. Les PDF que le moteur de lecture refuse ne pourront pas etre
repares. Sur un flux de factures reelles, cela represente typiquement quelques
pourcents des documents.
"@
}

# --- Fin ----------------------------------------------------------------------

Write-Host ""
Write-Host "Installation terminee." -ForegroundColor Green
Write-Host ""
Write-Host "Etapes restantes :" -ForegroundColor White
Write-Host "  1. Renseigner les boites et les cibles dans $settingsPath"
Write-Host "  2. Demarrer le service : Start-Service $serviceName"
Write-Host "  3. Les secrets saisis en clair sont chiffres au premier demarrage."
Write-Host "     SUPPRIMER ensuite les fichiers appsettings.json.clear.*.bak"
Write-Host "  4. L'interface se connecte seule : le jeton lui a ete depose."