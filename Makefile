# Makefile ACPoller
#
# Prerequis : GNU Make, SDK .NET 10, NSIS 3.
#   choco install make nsis
#
# Cibles :
#   make             publie et construit l'installeur
#   make test        execute les tests
#   make version V=2.1.0
#   make clean
#   make info        affiche la configuration detectee
#
# Aucun appel a PowerShell ni a $(file <) : Make ne voit pas toujours le meme
# PATH que la console, et $(file <) demande Make 4.2. La version vit donc dans
# version.mk, INCLUS par ce fichier, mecanisme compris par toutes les versions
# de Make. Version.props fait la meme chose pour MSBuild, et la cible version
# ecrit les deux ensemble.

include version.mk

# Une version vide produit un installeur nomme ACPoller--setup.exe, sans
# numero, et personne ne le remarque avant la livraison. Autant s'arreter ici.
ifeq ($(strip $(VERSION)),)
$(error VERSION non definie. Verifier version.mk)
endif

SHELL := cmd.exe
.SHELLFLAGS := /C

CONFIG       := Release
INSTALLER    := installer
BIN_OUT      := $(INSTALLER)/bin
SETUP        := $(INSTALLER)/ACPoller-$(VERSION)-setup.exe

# Conversion des separateurs pour les commandes cmd, qui refusent les barres
# obliques dans un chemin passe a del, rmdir ou copy.
BIN_WIN      := $(subst /,\,$(BIN_OUT))
INST_WIN     := $(subst /,\,$(INSTALLER))

# NSIS n'est pas dans le PATH apres une installation standard.
MAKENSIS     := "C:\NSIS\makensis.exe"

.PHONY: all info restore build test publish check installer version clean rebuild

all: installer

info:
	@echo Version    : $(VERSION)
	@echo Config     : $(CONFIG)
	@echo Sortie     : $(BIN_OUT)
	@echo Installeur : $(SETUP)
	@echo NSIS       : $(MAKENSIS)

restore:
	dotnet restore

build: restore
	dotnet build -c $(CONFIG) --no-restore

test: build
	dotnet test -c $(CONFIG) --no-build

# Publication dans un repertoire COMMUN : le service et l'interface partagent
# le meme runtime autonome et les memes assemblages du socle. Deux repertoires
# separes dupliqueraient environ soixante-dix mega-octets identiques.
#
# Le repertoire est vide au prealable : sinon les fichiers d'une version
# precedente subsistent et se retrouvent dans l'installeur, qui grossit a
# chaque livraison avec des assemblages fantomes.
publish: test
	@if exist "$(BIN_WIN)" rmdir /s /q "$(BIN_WIN)"
	dotnet publish src/ACPoller.Service/ACPoller.Service.csproj -c $(CONFIG) -o $(BIN_OUT)
	dotnet publish src/ACPoller.Ui/ACPoller.Ui.csproj -c $(CONFIG) -o $(BIN_OUT)
	@copy /y "$(INST_WIN)\install-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\uninstall-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\configure-tools.ps1" "$(BIN_WIN)\" >nul

# Barriere avant packaging : une publication partielle produirait un installeur
# sans interface, et le defaut ne se verrait qu'a la recherche du raccourci sur
# le poste client.
check:
	@if not exist "$(BIN_WIN)\ACPoller.Service.exe" (echo ERREUR: service non publie & exit /b 1)
	@if not exist "$(BIN_WIN)\ACPoller.Ui.exe" (echo ERREUR: interface non publiee & exit /b 1)

# La version est passee au script NSIS plutot que codee dedans : une seule
# source de verite pour les assemblages et l'installeur.
installer: publish check
	$(MAKENSIS) /DVERSION=$(VERSION) "$(INST_WIN)\ACPoller.nsi"
	@echo Installeur genere : $(SETUP)

# Les deux fichiers sont ecrits ensemble : version.mk pour Make, Version.props
# pour MSBuild. Une seule commande, donc aucun risque de divergence.
version:
	@if "$(V)"=="" (echo Usage: make version V=2.1.0 & exit /b 1)
	@echo VERSION := $(V)> version.mk
	@echo ^<Project^>^<PropertyGroup^>^<VersionPrefix^>$(V)^</VersionPrefix^>^</PropertyGroup^>^</Project^>> Version.props
	@echo Version portee a $(V)


rebuild: clean all

# dotnet clean ne supprime QUE les sorties qu'il connait : un fichier retire du
# projet laisse son assemblage dans obj, et la publication suivante peut le
# reprendre. Pour une livraison, seule la suppression physique garantit que le
# paquet ne contient que ce qui est dans le depot.
# Le parcours est limite aux repertoires de CODE : une recherche depuis la
# racine emporterait installer\prereq\qpdf\bin, qui n'est pas une sortie de
# compilation mais un outil tiers a livrer.
clean:
	dotnet clean -c $(CONFIG)
	@for /d /r src %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@for /d /r tests %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@for /d /r samples %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@if exist "$(BIN_WIN)" rmdir /s /q "$(BIN_WIN)"
	@if exist "$(INST_WIN)\ACPoller-*-setup.exe" del /q "$(INST_WIN)\ACPoller-*-setup.exe"
# Les sauvegardes de configuration n'ont RIEN a faire dans un installeur : les
# .clear.*.bak portent les secrets en clair du poste de developpement, et ils
# seraient deployes tels quels chez le client.
publish: test
	@if exist "$(BIN_WIN)" rmdir /s /q "$(BIN_WIN)"
	dotnet publish src/ACPoller.Service/ACPoller.Service.csproj -c $(CONFIG) -o $(BIN_OUT)
	dotnet publish src/ACPoller.Ui/ACPoller.Ui.csproj -c $(CONFIG) -o $(BIN_OUT)
	@if exist "$(BIN_WIN)\*.bak" del /q "$(BIN_WIN)\*.bak"
	@if exist "$(BIN_WIN)\ui-settings.json" del /q "$(BIN_WIN)\ui-settings.json"
	@copy /y "$(INST_WIN)\install-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\uninstall-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\configure-tools.ps1" "$(BIN_WIN)\" >nul

# Cible de LIVRAISON : repart de zero. Plus lente, mais c'est la seule qui
# garantit que l'installeur ne contient que le contenu du depot.
.PHONY: release
release: clean installer
	@echo.
	@echo Livraison prete : $(SETUP)
