# Corpus de messages atypiques

Chaque fichier reproduit un mode de defaillance observe ou redoute. Le corpus
est la vraie valeur des tests : il grossit a chaque incident de production, et
un incident deja represente ne peut plus revenir.

| Fichier | Piege | Ce qui doit se passer |
|---|---|---|
| 01-sans-corps.eml | Aucune partie texte ni HTML | Aucun noeud de corps, pas de NullReference (incident prod v1) |
| 02-html-seul.eml | HTML sans alternative texte | Corps en corps.html |
| 03-noms-doublon.eml | Deux `facture.pdf` | Deux fichiers distincts, aucun ecrasement |
| 04-eml-imbrique.eml | Message transfere avec PJ | Developpe comme un message recu |
| 05-eml-triple.eml | Trois niveaux d'imbrication | Borne par MaxDepth, niveau excedentaire exclu |
| 06-zip-dans-zip.eml | Archive dans archive | Zip interne expose comme conteneur |
| 07-zip-slip.eml | Entree `../../../evil.txt` | Refusee, rien hors du repertoire de travail |
| 08-bombe.eml | 20 Mo de zeros, taux > 1000:1 | Entree refusee sur le taux de compression |
| 09-piece-sans-nom.eml | PJ sans filename | Nom deduit du type MIME, extension correcte |
| 10-nom-long.eml | Nom de 300 caracteres | Tronque, extension preservee |
| 11-cp1252.eml | Texte Windows-1252 accentue | Lu en Latin-1, accents preserves |
| 12-pdf-corrompu.eml | PDF a xref detruit | Reparation tentee, sinon substitution |
| 13-image-inline.eml | Logo de signature en cid | Classe InlineImage, filtre sur la taille |
| 14-nom-hostile.eml | `..\..\fac:ture<1>\|"?.pdf` | Assaini, ecriture dans le repertoire de travail |
| 15-sans-message-id.eml | Pas de Message-Id | Deduplication par empreinte |
| 16-piece-vide.eml | PJ de zero octet | Conservee dans l'arbre, taille nulle |
| 17-sujet-hostile.eml | Separateurs, guillemets dans le sujet | Nommage et echappement CSV corrects |

## Regle

Tout incident de production donne lieu a un nouveau fichier ici AVANT le
correctif. Sans quoi la correction n'est verifiee qu'une fois, a la main.
