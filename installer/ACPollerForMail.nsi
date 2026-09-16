; Installeur ACPollerForMail
;
; Le service et l'interface sont publies en autonome : aucun runtime .NET a
; installer sur le serveur. L'installeur ne fait donc que deposer des fichiers,
; creer le service et poser les droits.
;
; Prerequis de compilation, dans le repertoire installer :
;   bin\                  publie par make publish (service ET interface)
;   acpoller.ico
;   licence.txt
;   prereq\LibreOffice.msi   optionnel
;   prereq\qpdf\             optionnel
;
; Ordre a respecter a chaque livraison :
;   make            (publie puis compile l'installeur)
; ou a la main :
;   installer\publish.cmd
;   makensis installer\ACPollerForMail.nsi

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"
!include "StrFunc.nsh"

; La version vient du Makefile (/DVERSION=...), avec un repli pour une
; compilation manuelle. Une seule source de verite : le fichier VERSION.
!ifndef VERSION
  !define VERSION "2.0.0"
!endif

!define PRODUCT "ACPollerForMail"
!define PUBLISHER "Arondor"
!define SERVICE "ACPollerForMail"

Name "${PRODUCT} ${VERSION}"
OutFile "ACPollerForMail-${VERSION}-setup.exe"
InstallDir "$PROGRAMFILES64\${PRODUCT}"
InstallDirRegKey HKLM "Software\${PRODUCT}" "InstallPath"
RequestExecutionLevel admin
Unicode true

; Verification a la COMPILATION : un repertoire de publication vide ne produit
; qu'un avertissement 7010, l'installeur se construit sans l'interface, et
; personne ne s'en apercoit avant de chercher le raccourci.
!if /FileExists "bin\ACPoller.Ui.exe"
!else
  !error "installer\bin\ACPoller.Ui.exe absent. Lancer 'make publish' avant de compiler l'installeur."
!endif

!if /FileExists "bin\ACPoller.Service.exe"
!else
  !error "installer\bin\ACPoller.Service.exe absent. Lancer 'make publish' avant de compiler l'installeur."
!endif

!define MUI_ABORTWARNING
!define MUI_ICON "acpoller.ico"
!define MUI_UNICON "acpoller.ico"

!define MUI_PAGE_CUSTOMFUNCTION_SHOW WelcomeShow
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "licence.txt"
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPre
!insertmacro MUI_PAGE_DIRECTORY
Page custom ServiceAccountPage ServiceAccountLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; VersionCompare compare deux numeros de version : 0 identiques, 1 la
; premiere est plus recente, 2 la seconde l'est.
!insertmacro VersionCompare

; StrFunc demande une declaration explicite de chaque fonction utilisee.
${StrStr}

!insertmacro MUI_LANGUAGE "French"

Var ServiceAccount
Var ServicePassword

; Vrai quand une installation precedente a ete trouvee. L'assistant se
; comporte alors en mise a jour : le repertoire n'est plus demandé, il est
; impose.
Var Existant
Var AncienNom
Var VersionInstallee
Var Dialog
Var AccountField
Var PasswordField

; ------------------------------------------------------------------------------
; Page de saisie du compte de service
;
; Page dediee et non simple case a cocher : le compte de service est la decision
; d'installation la plus lourde de consequences. Le compte systeme local n'a
; AUCUNE identite reseau, donc ni partage UNC ni FTP interne, et l'erreur ne se
; manifeste qu'au premier export.
; ------------------------------------------------------------------------------


; ------------------------------------------------------------------------------
; Detection de l'installation existante
;
; Une reinstallation doit REPARER en place, jamais s'installer a cote. Deux
; installations parallelles produiraient deux services concurrents collectant
; les memes boites, donc des doublons en GED, et le second ecraserait les
; fichiers deposes par le premier.
;
; La cle de registre fait foi, pas le repertoire par defaut : l'exploitant a pu
; installer ailleurs, et c'est son choix qu'il faut respecter.
; ------------------------------------------------------------------------------

Function .onInit
  StrCpy $Existant "0"
  StrCpy $AncienNom "0"
  StrCpy $VersionInstallee ""

  ; Une seule instance de l'assistant a la fois. Deux installations
  ; simultanees se disputeraient les memes fichiers et le meme service, avec
  ; un resultat imprevisible selon celle qui gagne la course.
  System::Call 'kernel32::CreateMutex(p 0, i 0, t "ACPollerForMailSetup") p .r1 ?e'
  Pop $0

  ${If} $0 <> 0
    MessageBox MB_OK|MB_ICONEXCLAMATION \
      "Une installation d'${PRODUCT} est deja en cours."
    Abort
  ${EndIf}

  ; Ecran d'accueil, affiche pendant la decompression. Le chemin est passe SANS
  ; extension : le plugin ajoute .bmp de lui-meme, et avec l'extension il
  ; cherche splash.bmp.bmp, ne trouve rien, et n'affiche rien sans message.
  InitPluginsDir
  File /oname=$PLUGINSDIR\splash.bmp "splash.bmp"
  advsplash::show 1500 400 0 -1 $PLUGINSDIR\splash
  Pop $0
  Delete $PLUGINSDIR\splash.bmp

  ; 1. Installation du meme produit
  ReadRegStr $0 HKLM "Software\${PRODUCT}" "InstallPath"

  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\*.*"
    StrCpy $INSTDIR $0
    StrCpy $Existant "1"
    Goto init_done
  ${EndIf}

  ; 2. Installation sous l'ANCIEN nom, avant le renommage. La reprendre en
  ;    place evite de laisser deux produits installes dont un seul est a jour.
  ReadRegStr $0 HKLM "Software\ACPoller" "InstallPath"

  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\*.*"
    StrCpy $INSTDIR $0
    StrCpy $Existant "1"
    StrCpy $AncienNom "1"
    Goto init_done
  ${EndIf}

  init_done:

  ; ----------------------------------------------------------------------------
  ; Controle de l'existant
  ;
  ; Trois questions sont posees dans l'ordre ou elles comptent : le service
  ; tourne-t-il, quelle version est en place, et l'exploitant veut-il vraiment
  ; poursuivre. Installer par-dessus sans rien dire est le comportement qui
  ; produit les incidents les plus difficiles a comprendre apres coup.
  ; ----------------------------------------------------------------------------

  ${If} $Existant == "1"
    ReadRegStr $VersionInstallee HKLM "Software\${PRODUCT}" "Version"

    ${If} $VersionInstallee == ""
      ReadRegStr $VersionInstallee HKLM "Software\ACPoller" "Version"
    ${EndIf}

    ${If} $VersionInstallee == ""
      StrCpy $VersionInstallee "inconnue"
    ${EndIf}

    ; Retour en arriere : le refuser serait excessif, un exploitant peut avoir
    ; une bonne raison de revenir a une version precedente apres un incident.
    ; Mais il doit le faire en connaissance de cause, car la configuration
    ; ecrite par une version recente peut contenir des reglages que l'ancienne
    ; ne comprend pas et ecartera.
    ${VersionCompare} "${VERSION}" "$VersionInstallee" $0

    ${If} $0 == 2
      MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 \
        "La version $VersionInstallee est installee dans $INSTDIR.$\r$\n$\r$\nVous allez installer la version ${VERSION}, PLUS ANCIENNE.$\r$\n$\r$\nLa configuration ecrite par la version en place peut contenir des reglages que celle-ci ne comprend pas : les configurations concernees seraient ecartees au demarrage.$\r$\n$\r$\nPoursuivre malgre tout ?" \
        IDYES suite_version
      Abort
      suite_version:
    ${ElseIf} $0 == 0
      MessageBox MB_YESNO|MB_ICONQUESTION \
        "La version ${VERSION} est deja installee dans $INSTDIR.$\r$\n$\r$\nReinstaller par-dessus ? Les binaires seront remplaces, la configuration, les secrets et le journal des messages traites sont conserves." \
        IDYES suite_version2
      Abort
      suite_version2:
    ${EndIf}

    ; Le service en cours d'execution n'empeche pas l'installation : la section
    ; principale l'arrete. Mais la capture s'interrompt, et ce n'est pas une
    ; chose a decouvrir apres coup un jour de forte charge.
    nsExec::ExecToStack 'sc.exe query ${SERVICE}'
    Pop $0
    Pop $1

    ${If} $AncienNom == "1"
      nsExec::ExecToStack 'sc.exe query ACPoller'
      Pop $0
      Pop $1
    ${EndIf}

    ; StrStr retourne la chaine a partir de la position trouvee, ou une chaine
    ; vide si le motif est absent. La sortie de sc.exe contient "RUNNING" quand
    ; le service tourne, "STOPPED" sinon.
    ${StrStr} $2 $1 "RUNNING"

    ${If} $2 != ""
      MessageBox MB_YESNO|MB_ICONEXCLAMATION \
        "Le service est en cours d'execution.$\r$\n$\r$\nIl sera arrete pendant l'installation : la capture des messages est interrompue, et les traitements en cours reprendront au redemarrage.$\r$\n$\r$\nPoursuivre ?" \
        IDYES suite_service
      Abort
      suite_service:
    ${EndIf}
  ${EndIf}

FunctionEnd

; Le choix du repertoire est saute sur une mise a jour : proposer de changer de
; chemin quand une installation existe revient a proposer de la dupliquer.
Function DirectoryPre
  ${If} $Existant == "1"
    Abort
  ${EndIf}
FunctionEnd

; Le texte de la page d'accueil change selon le contexte : une mise a jour et
; une premiere installation n'appellent pas les memes precautions, et un texte
; generique ne previent de rien.
Function WelcomeShow
  ${If} $Existant == "1"
    ${If} $AncienNom == "1"
      SendMessage $mui.WelcomePage.Text ${WM_SETTEXT} 0 "STR:Une installation sous l'ancien nom ACPoller a ete trouvee dans $INSTDIR.$\r$\n$\r$\nElle sera mise a jour EN PLACE : la configuration, les secrets et le journal des messages traites sont conserves. L'ancien service sera remplace par le nouveau.$\r$\n$\r$\nLe repertoire d'installation ne peut pas etre change : deux installations parallelles collecteraient les memes boites."
    ${Else}
      SendMessage $mui.WelcomePage.Text ${WM_SETTEXT} 0 "STR:Une installation existante a ete trouvee dans $INSTDIR.$\r$\n$\r$\nElle sera reparee ou mise a jour EN PLACE : la configuration, les secrets et le journal des messages traites sont conserves.$\r$\n$\r$\nLe repertoire d'installation ne peut pas etre change."
    ${EndIf}
  ${EndIf}
FunctionEnd

Function ServiceAccountPage
  !insertmacro MUI_HEADER_TEXT "Compte de service" "Sous quel compte le service doit-il tourner ?"

  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 40u \
    "Laisser vide pour utiliser le compte systeme local. Attention : ce compte n'a AUCUN acces reseau. Les depots sur partage UNC et les connexions FTP internes echoueront, et l'erreur n'apparaitra qu'au premier message traite."
  Pop $0

  ${NSD_CreateLabel} 0 48u 100% 12u "Compte (DOMAINE\utilisateur) :"
  Pop $0
  ${NSD_CreateText} 0 62u 100% 12u ""
  Pop $AccountField

  ${NSD_CreateLabel} 0 82u 100% 12u "Mot de passe :"
  Pop $0
  ${NSD_CreatePassword} 0 96u 100% 12u ""
  Pop $PasswordField

  nsDialogs::Show
FunctionEnd

Function ServiceAccountLeave
  ${NSD_GetText} $AccountField $ServiceAccount
  ${NSD_GetText} $PasswordField $ServicePassword
FunctionEnd

; ------------------------------------------------------------------------------
; Service
; ------------------------------------------------------------------------------

Section "Service et interface" SEC_MAIN
  SectionIn RO
  SetOutPath "$INSTDIR"

  ; Arret prealable : les fichiers d'un service en cours sont verrouilles et la
  ; copie echouerait silencieusement sur certains d'entre eux.
  DetailPrint "Arret du service s'il existe..."
  nsExec::ExecToLog 'sc.exe stop ${SERVICE}'
  Pop $0

  ; Ancien service, avant le renommage. Le laisser en place ferait tourner DEUX
  ; services sur les memes boites : chacun collecterait les memes messages, et
  ; le journal d'idempotence de l'un ignore ce que l'autre a livre.
  ${If} $AncienNom == "1"
    DetailPrint "Suppression de l'ancien service ACPoller..."
    nsExec::ExecToLog 'sc.exe stop ACPoller'
    Pop $0
    Sleep 2000
    nsExec::ExecToLog 'sc.exe delete ACPoller'
    Pop $0
  ${EndIf}

  Sleep 3000

  ; Un seul repertoire pour les deux applications : elles partagent le meme
  ; runtime autonome et les memes assemblages du socle. Deux repertoires
  ; separes dupliqueraient environ soixante-dix mega-octets identiques.
  File /r /x appsettings.json "bin\*.*"

  ; La configuration n'est deposee QUE si elle n'existe pas : une mise a jour
  ; ne doit jamais ecraser le parametrage de dizaines de boites, ni les secrets
  ; chiffres qu'il contient.
  ${IfNot} ${FileExists} "$INSTDIR\appsettings.json"
    DetailPrint "Depot de la configuration initiale."
    File "/oname=$INSTDIR\appsettings.json" "bin\appsettings.json"
  ${Else}
    DetailPrint "Configuration existante conservee."
    ; Copie de reference : permet de reprendre les reglages ajoutes par la
    ; nouvelle version sans deviner ce qui a change.
    File "/oname=$INSTDIR\appsettings.json.nouveau" "bin\appsettings.json"
  ${EndIf}

  SetOutPath "$INSTDIR\stylesheets"
  File /nonfatal /r "stylesheets\*.*"

  SetOutPath "$INSTDIR\fonts"
  File /nonfatal /r "fonts\*.*"

  SetOutPath "$INSTDIR"

  CreateDirectory "$SMPROGRAMS\${PRODUCT}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\${PRODUCT}.lnk" "$INSTDIR\ACPoller.Ui.exe"

  WriteRegStr HKLM "Software\${PRODUCT}" "InstallPath" "$INSTDIR"
  WriteRegStr HKLM "Software\${PRODUCT}" "Version" "${VERSION}"
SectionEnd

; ------------------------------------------------------------------------------
; Outils tiers, optionnels
;
; Ni LibreOffice ni qpdf ne sont redistribues au sens de leur licence : ils sont
; APPELES hors processus, jamais lies. Leur installation reste donc a la main de
; l'exploitant, mais la proposer ici evite l'oubli le plus courant.
;
; Les deux sections ne s'activent que si le fichier correspondant est present
; dans installer\prereq. Une compilation sans ces fichiers produit un installeur
; valide, simplement sans les outils.
; ------------------------------------------------------------------------------

Section /o "LibreOffice (conversion bureautique)" SEC_LIBREOFFICE
  ; Deja installe : on ne reinstalle pas, la version en place peut avoir ete
  ; choisie deliberement.
  ${If} ${FileExists} "$PROGRAMFILES64\LibreOffice\program\soffice.exe"
    DetailPrint "LibreOffice deja present, installation ignoree."
    Goto libreoffice_done
  ${EndIf}

  SetOutPath "$TEMP"
  File /nonfatal "prereq\LibreOffice.msi"

  ${IfNot} ${FileExists} "$TEMP\LibreOffice.msi"
    DetailPrint "Paquet LibreOffice absent de l'installeur."
    Goto libreoffice_done
  ${EndIf}

  DetailPrint "Installation de LibreOffice, patienter..."

  ; Installation silencieuse minimale : ni raccourcis, ni association de
  ; fichiers, ni mise a jour automatique. C'est un composant de service, il ne
  ; doit pas apparaitre comme une suite bureautique sur le serveur, ni se
  ; mettre a jour sans controle sous les pieds du service.
  nsExec::ExecToLog 'msiexec.exe /i "$TEMP\LibreOffice.msi" /qn /norestart CREATEDESKTOPLINK=0 REGISTER_ALL_MSO_TYPES=0 ISCHECKFORPRODUCTUPDATES=0 UI_LANGS=fr'
  Pop $0

  ${If} $0 != 0
    DetailPrint "Installation de LibreOffice terminee avec le code $0."
  ${EndIf}

  Delete "$TEMP\LibreOffice.msi"

  libreoffice_done:
SectionEnd

Section /o "qpdf (reparation des PDF)" SEC_QPDF
  ; qpdf est portable : une simple copie suffit, pas d'installeur a executer.
  ; Il est depose SOUS le repertoire du produit, ce qui garantit la version
  ; utilisee et evite de dependre d'une installation systeme.
  SetOutPath "$INSTDIR\qpdf"
  File /nonfatal /r "prereq\qpdf\*.*"

  ${If} ${FileExists} "$INSTDIR\qpdf\bin\qpdf.exe"
    DetailPrint "qpdf installe dans $INSTDIR\qpdf"
  ${Else}
    DetailPrint "Fichiers qpdf absents de l'installeur."
  ${EndIf}
SectionEnd

; ------------------------------------------------------------------------------
; Configuration du service, toujours executee
; ------------------------------------------------------------------------------

Section "-Configuration du service"
  SetOutPath "$INSTDIR"

  DetailPrint "Creation des repertoires, des droits et du service..."

  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\install-service.ps1" -InstallPath "$INSTDIR" -ServiceAccount "$ServiceAccount" -ServicePassword "$ServicePassword"'
  Pop $0

  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "La configuration du service a echoue (code $0). Consulter le journal d'installation ci-dessus."
  ${EndIf}

  ; Chemins des outils renseignes dans la configuration : sans cela,
  ; l'exploitant doit les saisir a la main, et l'oubli ne se manifeste qu'a la
  ; premiere piece bureautique ou au premier PDF a reparer.
  DetailPrint "Detection des outils de conversion..."
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\configure-tools.ps1" -SettingsPath "$INSTDIR\appsettings.json"'
  Pop $0

  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Taille reportee dans Programmes et fonctionnalites : une publication
  ; autonome pese pres de cent mega-octets, autant que l'exploitant le sache.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2

  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "DisplayName" "${PRODUCT} ${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "DisplayIcon" "$INSTDIR\ACPoller.Ui.exe"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "EstimatedSize" "$0"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}" \
    "NoRepair" 1

  ; Entrees de l'ancien nom retirees une fois la reprise faite : sans cela,
  ; Programmes et fonctionnalites afficherait deux produits dont un fantome,
  ; et sa desinstallation supprimerait le service actuel.
  ${If} $AncienNom == "1"
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ACPoller"
    DeleteRegKey HKLM "Software\ACPoller"
    Delete "$SMPROGRAMS\ACPoller\ACPoller.lnk"
    RMDir "$SMPROGRAMS\ACPoller"
  ${EndIf}
SectionEnd

; ------------------------------------------------------------------------------
; Descriptions des composants
; ------------------------------------------------------------------------------

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN} \
    "Service Windows de capture et interface de supervision. Les deux partagent le meme repertoire et le meme runtime."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_LIBREOFFICE} \
    "Sans LibreOffice, aucune piece bureautique n'est convertie : Word, Excel et PowerPoint sortent en page de substitution."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_QPDF} \
    "Sans qpdf, les PDF que le moteur de lecture refuse ne sont pas reparables. Sur un flux de factures reelles, cela represente quelques pourcents des documents."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ------------------------------------------------------------------------------
; Desinstallation
; ------------------------------------------------------------------------------

Section "Uninstall"
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall-service.ps1"'
  Pop $0

  Delete "$SMPROGRAMS\${PRODUCT}\${PRODUCT}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT}"

  RMDir /r "$INSTDIR\qpdf"
  Delete "$INSTDIR\uninstall.exe"

  ; LibreOffice n'est PAS desinstalle : il a pu etre installe avant le produit,
  ; ou servir a autre chose sur ce serveur. Le retirer serait presomptueux.

  ; La configuration et les donnees sont CONSERVEES. appsettings.json porte les
  ; secrets chiffres et le parametrage de dizaines de boites ; le journal des
  ; messages traites, sous ProgramData, evite un retraitement complet a la
  ; reinstallation. Les supprimer produirait des doublons en GED.
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}"
  DeleteRegKey HKLM "Software\${PRODUCT}"

  MessageBox MB_ICONINFORMATION \
    "Desinstallation terminee.$\r$\n$\r$\nCONSERVES volontairement :$\r$\n  - $INSTDIR (configuration et secrets)$\r$\n  - ProgramData\${PRODUCT} (journal des messages traites)$\r$\n$\r$\nLes supprimer manuellement pour une desinstallation definitive. Attention : effacer le journal des messages traites provoquera un retraitement de l'historique a la prochaine installation."
SectionEnd
