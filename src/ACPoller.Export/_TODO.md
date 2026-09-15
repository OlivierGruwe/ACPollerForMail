# ACPoller.Export

## A implementer

| Cible | Points d'attention |
|---|---|
| `FileSystemExportTarget` | ecriture `.tmp` puis `Move`, fichier temoin en dernier |
| `FtpExportTarget` | FTPS explicite, reprise, pas de validation TLS desactivee |
| `S3ExportTarget` | multipart au-dela d'un seuil, SSE, prefixe par jetons |
| `PluginExportTarget` | delegation a une `IExportTargetFactory` de plugin client |

## Invariant

`ExportPolicy.AllOrNothing` : si une cible du lot echoue, `RollbackAsync` est appele
sur les cibles deja ecrites. Une GED ne doit jamais voir un message a moitie depose.
