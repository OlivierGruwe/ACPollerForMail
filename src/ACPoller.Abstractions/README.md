# ACPoller.Abstractions

Contrat public d'extension d'ACPoller v2. Cible `net10.0` (neutre, testable hors Windows).

## Ce que le contrat impose

| Invariant | Raison |
|---|---|
| Tout est `async` + `CancellationToken` | Aucun `.Result`, aucun figeage de worker |
| Tout echec est qualifie (`FailureKind`) | Le retry ne devine plus si l'erreur est rejouable |
| Le proprietaire d'un `Stream` le libere en `using` | Cause racine historique des verrous fichiers |
| L'arbre documentaire porte l'etat par noeud | Reprise par etape, pas de retraitement complet |
| La cle de deduplication est le `Message-Id` | L'id Graph change quand le message est deplace |
| Les dossiers bien connus sont canoniques | Les libelles localises cassent `inbox=Inbox` |
| L'extraction recursive est bornee | Zip bomb, eml infini, chemins traversants |
| L'export est atomique et transactionnel | Une GED ne doit jamais lire un PDF incomplet |
| Les plugins sont resolus par interface | Fin de la reflexion sur signature (`applyAssembly`) |

## Points d'extension

| Interface | Role | Implementations socle |
|---|---|---|
| `IMailSource` | Collecte | IMAP (MailKit), Graph |
| `IContainerExtractor` | Zip, eml, msg | Zip, Mime, Msg |
| `IDocumentConverter` | Vers PDF | LibreOffice, Image, Texte, PdfPassThrough |
| `IPdfAssembler` | Fusion, reparation | PDFsharp, qpdf |
| `IMetadataWriter` | Fichier d'info | Json, Xml, Csv |
| `IExportTarget` | Depot | Fs, Ftps, S3, Plugin |
| `ICaptureProcessor` | Metier client | (aucune, cote client) |

## Regles de versionnement

- `ContractVersion.Current` s'incremente a chaque rupture binaire.
- Un plugin declare sa version via `[assembly: AcPollerPlugin(1, typeof(MonPlugin))]`.
- Le loader refuse un plugin incompatible avec un message explicite, avant instanciation.
- Ajout d'un membre a une interface publique = rupture. Passer par une nouvelle interface
  ou une propriete optionnelle sur un record.

## Regles de codage non negociables

- `throw;` jamais `throw ex;` (analyseur a activer en CI).
- Aucune reference a MimeKit, Graph, AWS, PDFsharp dans ce projet.
- `TreatWarningsAsErrors` actif, nullable actif.
