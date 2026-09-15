# Makefile ACPoller
#
# Prerequis : GNU Make, SDK .NET 10, NSIS 3.
#   choco install make nsis
#
# Pour la documentation, en plus :
#   choco install pandoc wkhtmltopdf
#   pip install pypdf reportlab pillow
#
# Cibles :
#   make             publie et construit l'installeur
#   make release     repart de zero, regenere la documentation, livre
#   make docs        regenere les PDF depuis les sources markdown
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
DOCS         := docs
SETUP        := $(INSTALLER)/ACPoller-$(VERSION)-setup.exe

# Conversion des separateurs pour les commandes cmd, qui refusent les barres
# obliques dans un chemin passe a del, rmdir ou copy.
BIN_WIN      := $(subst /,\,$(BIN_OUT))
INST_WIN     := $(subst /,\,$(INSTALLER))
DOCS_WIN     := $(subst /,\,$(DOCS))

# NSIS n'est pas dans le PATH apres une installation standard.
MAKENSIS     := "C:\NSIS\makensis.exe"

.PHONY: all info restore build test publish check check-docs installer docs release version clean rebuild \
        git-check commit push tag ship

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
	@if exist "$(BIN_WIN)\*.bak" del /q "$(BIN_WIN)\*.bak"
	@if exist "$(BIN_WIN)\ui-settings.json" del /q "$(BIN_WIN)\ui-settings.json"
	@copy /y "$(INST_WIN)\install-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\uninstall-service.ps1" "$(BIN_WIN)\" >nul
	@copy /y "$(INST_WIN)\configure-tools.ps1" "$(BIN_WIN)\" >nul

# Barriere avant packaging : une publication partielle produirait un installeur
# sans interface, et le defaut ne se verrait qu'a la recherche du raccourci sur
# le poste client.
check:
	@if not exist "$(BIN_WIN)\ACPoller.Service.exe" (echo ERREUR: service non publie & exit /b 1)
	@if not exist "$(BIN_WIN)\ACPoller.Ui.exe" (echo ERREUR: interface non publiee & exit /b 1)

# Regeneration des PDF depuis les sources markdown, dans les deux langues.
#
# Cible SEPAREE de la construction courante : la chaine demande pandoc,
# wkhtmltopdf et trois paquets Python, que tout poste de developpement n'a pas.
# Un 'make' quotidien ne doit pas echouer parce qu'un outil de documentation
# manque.
docs:
	@python "$(DOCS_WIN)\generer.py" fr || exit /b 1
	@python "$(DOCS_WIN)\generer.py" en || exit /b 1
	@echo Documentation regeneree.

# Controle que les PDF sont a jour. Compare grossierement les dates : une
# source plus recente que son PDF signale un oubli de regeneration, et une
# documentation qui decrit la version precedente est pire que pas de
# documentation du tout.
check-docs:
	@for %%f in ("$(DOCS_WIN)\fr\*.md" "$(DOCS_WIN)\en\*.md") do @( \
	  if not exist "%%~dpnf.pdf" (echo ERREUR: %%~nxf sans PDF & exit /b 1) )
	@echo PDF presents pour toutes les sources.

# La version est passee au script NSIS plutot que codee dedans : une seule
# source de verite pour les assemblages et l'installeur.
installer: publish check
	$(MAKENSIS) /DVERSION=$(VERSION) "$(INST_WIN)\ACPoller.nsi"
	@echo Installeur genere : $(SETUP)

# Cible de LIVRAISON : repart de zero, regenere la documentation, puis
# construit l'installeur. Plus lente, mais c'est la seule qui garantit que le
# paquet ne contient que le contenu du depot et que les PDF livres decrivent
# bien la version livree.
release: clean docs installer
	@echo.
	@echo Livraison $(VERSION) prete.
	@echo   Installeur    : $(SETUP)
	@echo   Documentation : $(DOCS)\fr et $(DOCS)\en
	@echo.
	@echo Penser a etiqueter : git tag v$(VERSION) ^&^& git push --tags

# Les deux fichiers sont ecrits ensemble : version.mk pour Make, Version.props
# pour MSBuild. Une seule commande, donc aucun risque de divergence.
version:
	@if "$(V)"=="" (echo Usage: make version V=2.1.0 & exit /b 1)
	@echo VERSION := $(V)> version.mk
	@echo ^<Project^>^<PropertyGroup^>^<VersionPrefix^>$(V)^</VersionPrefix^>^</PropertyGroup^>^</Project^>> Version.props
	@echo Version portee a $(V)
	@echo Regenerer la documentation si elle mentionne le numero : make docs

# Le parcours est limite aux repertoires de CODE : une recherche depuis la
# racine emporterait installer\prereq\qpdf\bin, qui n'est pas une sortie de
# compilation mais un outil tiers a livrer.
clean:
	dotnet clean -c $(CONFIG)
	@for /d /r src %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@for /d /r tests %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@for /d /r samples %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d"
	@if exist "$(BIN_WIN)" rmdir /s /q "$(BIN_WIN)"
	@if exist "$(INST_WIN)\ACPollerForMail-*-setup.exe" del /q "$(INST_WIN)\ACPollerForMail-*-setup.exe"

rebuild: clean all

# ---------------------------------------------------------------------------
# Git
#
# Le push n'est PAS automatique dans release : une livraison qui pousse d'elle
# meme envoie l'erreur sur le distant avant qu'on ait pu la regarder. Les
# cibles ci-dessous sont explicites, et git-check s'interpose a chaque fois.
# ---------------------------------------------------------------------------

# Refuse tout ce qui ne doit jamais partir : configuration, jetons,
# sauvegardes en clair, binaires tiers, certificats. Le detail est dans
# verifier-index.cmd, plus lisible qu'une suite de findstr chainee ici.
git-check:
	@verifier-index.cmd

# Indexe tout et valide. Le message est obligatoire : un message automatique
# du type "maj" ne dit rien six mois plus tard, quand on cherche quand un
# comportement a change.
commit:
	@if "$(M)"=="" (echo Usage: make commit M="description du changement" & exit /b 1)
	git add -A
	@verifier-index.cmd || exit /b 1
	git commit -m "$(M)"

push:
	@verifier-index.cmd || exit /b 1
	git push

# Etiquette la version courante et la pousse. A faire APRES que l'installeur
# soit construit et verifie : une etiquette designe un etat livre, pas une
# intention de livrer.
tag:
	@git rev-parse "v$(VERSION)" >nul 2>nul && (echo ERREUR: l'etiquette v$(VERSION) existe deja. Passer a la version suivante avec make version V=x.y.z & exit /b 1) || ver >nul
	git tag -a "v$(VERSION)" -m "ACPoller $(VERSION)"
	git push origin "v$(VERSION)"
	@echo Etiquette v$(VERSION) poussee.

# Livraison complete. Le push du code reste separe et volontaire : l'etiquette
# ne fait que designer un commit deja pousse.
ship: release
	@echo.
	@echo Verifier l'installeur, puis :
	@echo   make commit M="version $(VERSION)"
	@echo   make push
	@echo   make tag
