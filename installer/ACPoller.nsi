; Installeur ACPoller
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
;   makensis installer\ACPoller.nsi

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

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

!if /FileExists "bin\appsettings.json.clear.*.bak"
  !error "Des sauvegardes en clair sont presentes dans installer\bin. Lancer 'make clean' avant de compiler l'installeur."
!endif

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

; La section qpdf est proposee mais ses fichiers sont absents : mieux vaut le
; dire a la compilation qu'apres coup, quand l'installeur a ete livre sans.
!if /FileExists "prereq\LibreOffice.msi"
  !if /FileExists "prereq\qpdf\bin\qpdf.exe"
  !else
    !warning "prereq\qpdf est incomplet : la section qpdf n'installera rien."
  !endif
!endif


!define MUI_ABORTWARNING
!define MUI_ICON "acpoller.ico"
!define MUI_UNICON "acpoller.ico"

; Bandeau lateral des pages Bienvenue et Fin.
!define MUI_WELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"

; Bandeau d'en-tete des pages internes. MUI_HEADERIMAGE_RIGHT place le logo a
; droite, du cote oppose au texte : a gauche, il chevauche le titre de page
; sur les libelles francais, plus longs que les anglais.
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_HEADERIMAGE_RIGHT

; Page de bienvenue, absente jusqu'ici : sans elle, le bandeau lateral n'a
; nulle part ou s'afficher.
!define MUI_WELCOMEPAGE_TITLE "ACPoller 2.0"
!define MUI_WELCOMEPAGE_TEXT "Capture de messagerie, conversion PDF et depot en GED.$\r$\n$\r$\nCet assistant installe le service et son interface de supervision.$\r$\n$\r$\nLe service sera arrete pendant l'installation si une version precedente est presente."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "licence.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
Page custom ServiceAccountPage ServiceAccountLeave
!insertmacro MUI_PAGE_INSTFILES

; Page de fin : elle propose d'ouvrir l'interface, ce qui evite a l'exploitant
; de la chercher dans le menu Demarrer juste apres l'installation.
!define MUI_FINISHPAGE_TITLE "Installation terminee"
!define MUI_FINISHPAGE_TEXT "Le service est installe et demarre.$\r$\n$\r$\nProchaine etape : declarer les boites a surveiller depuis l'interface de supervision."
!define MUI_FINISHPAGE_RUN "$INSTDIR\ACPoller.Ui.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Lancer l'interface de supervision"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\docs\INSTALLATION.pdf"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Ouvrir le guide d'installation"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "French"

Var ServiceAccount
Var ServicePassword
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

Function .onInit
  ; Extraction dans le repertoire temporaire : le plugin ne sait pas lire une
  ; ressource restee dans l'archive.
  InitPluginsDir
  File /oname=$PLUGINSDIR\splash.bmp "splash.bmp"

  ; 2000 ms d'affichage, sans fondu ni son. Le fondu ajoute une seconde
  ; perdue, et un son dans un assistant d'installation est deplace.
  advsplash::show 2000 0 0 -1 $PLUGINSDIR\splash.bmp

  Pop $0
  Delete $PLUGINSDIR\splash.bmp
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

  SetOutPath "$INSTDIR\docs"
  File /nonfatal "..\docs\INSTALLATION.pdf"
  File /nonfatal "..\docs\EXPLOITATION.pdf"
  
  SetOutPath "$INSTDIR"

  CreateDirectory "$SMPROGRAMS\${PRODUCT}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\${PRODUCT}.lnk" "$INSTDIR\ACPoller.Ui.exe"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\Guide d'exploitation.lnk" "$INSTDIR\docs\EXPLOITATION.pdf"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\Manuel.lnk" "$INSTDIR\docs\MANUEL.pdf"
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

  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\install-service.ps1" -InstallPath "$INSTDIR" -ServiceAccount "$ServiceAccount" -ServicePassword "$ServicePassword"'
  Pop $0

  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "La configuration du service a echoue (code $0). Consulter le journal d'installation ci-dessus."
  ${EndIf}

  ; Chemins des outils renseignes dans la configuration : sans cela,
  ; l'exploitant doit les saisir a la main, et l'oubli ne se manifeste qu'a la
  ; premiere piece bureautique ou au premier PDF a reparer.
  DetailPrint "Detection des outils de conversion..."
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\configure-tools.ps1" -SettingsPath "$INSTDIR\appsettings.json"'
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
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall-service.ps1"'
  Pop $0

  Delete "$SMPROGRAMS\${PRODUCT}\${PRODUCT}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT}"

  RMDir /r "$INSTDIR\qpdf"
  Delete "$INSTDIR\uninstall.exe"

  ; LibreOffice n'est PAS desinstalle : il a pu etre installe avant ACPoller,
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
