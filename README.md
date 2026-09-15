# ACPollerForMail

Service Windows de capture de messagerie : collecte IMAP, Microsoft Graph ou
repertoire, conversion PDF, depot en GED.

Reecriture complete d'ACPoller en .NET 10.

---

## Documentation

| Document | Francais | English |
|---|---|---|
| Manuel complet | [docs/fr/MANUEL.pdf](docs/fr/MANUEL.pdf) | [docs/en/MANUAL.pdf](docs/en/MANUAL.pdf) |
| Exploitation | [docs/fr/EXPLOITATION.pdf](docs/fr/EXPLOITATION.pdf) | [docs/en/OPERATIONS.pdf](docs/en/OPERATIONS.pdf) |
| Installation | [docs/fr/INSTALLATION.pdf](docs/fr/INSTALLATION.pdf) | [docs/en/SETUP.pdf](docs/en/SETUP.pdf) |

---

## Construire

```
make             publie et construit l'installeur
make test        execute les tests
make version V=2.1.0
make clean
```

Prerequis : SDK .NET 10, GNU Make, NSIS 3.

```
choco install make nsis
```

### Outils tiers de l'installeur

Les paquets LibreOffice et qpdf ne sont **pas** versionnes : trop volumineux
pour un depot de code, et disponibles chez leurs editeurs. Les deposer dans
`installer/prereq/` avant de construire l'installeur, en suivant
`installer/prereq/LISEZMOI.txt`.

Sans eux, l'installeur se construit quand meme : les deux sections optionnelles
n'installeront simplement rien.

---

## Structure

```
src/
  ACPoller.Abstractions     contrat public, stable, reference par les plugins
  ACPoller.Core             pipeline, workers, configuration, stockage
  ACPoller.Conversion       analyse MIME, extraction, conversion PDF
  ACPoller.Sources.Imap     collecte IMAP
  ACPoller.Sources.Graph    collecte Microsoft Graph
  ACPoller.Sources.Folder   lecture de fichiers .eml, pour le rejeu
  ACPoller.Export           depot systeme de fichiers, FTP, S3
  ACPoller.Service          hote du service Windows et API de pilotage
  ACPoller.Ui               interface de supervision, WPF
tests/                      tests unitaires
samples/                    plugin d'exemple
installer/                  script NSIS et scripts d'installation
docs/                       sources markdown et PDF livres
```

---

## Configuration

Le fichier de configuration n'est **pas** versionne : il porte les identifiants
de boites, les secrets clients et le jeton de pilotage.

Partir du modele :

```
copy src\ACPoller.Service\appsettings.sample.json ^
     src\ACPoller.Service\bin\Debug\net10.0-windows\win-x64\appsettings.json
```

Les secrets saisis en clair sont chiffres par le service au premier demarrage,
qui conserve une copie du fichier d'origine sous `.clear.*.bak`. **Ces copies
portent les secrets en clair** et doivent etre supprimees une fois le
chiffrement verifie.

---

## Points de conception a connaitre

Trois regles structurent le produit et se retrouvent partout dans le code.

**L'ordre de fin n'est pas negociable.** Depot confirme, puis marquage, puis
disposition, puis nettoyage. Un doublon se detecte et se corrige, une perte
silencieuse non.

**Une configuration fautive est ecartee, pas fatale.** Sur un parc de cent
boites, une faute de frappe ne doit pas empecher les quatre-vingt-dix-neuf
autres de tourner.

**L'interface n'ecrit jamais directement la configuration.** Elle dialogue avec
le service par son API locale, le service restant seul maitre de ce qu'il lit
et de quand il le relit.

Le detail figure au chapitre 2 du manuel.
