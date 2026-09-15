# ACPollerForMail — Guide d'installation

## Prerequis

| Element | Exigence |
|---|---|
| Systeme | Windows Server 2016 ou superieur |
| Runtime .NET | Aucun : la publication est autonome |
| LibreOffice | Optionnel, mais sans lui aucune piece bureautique n'est convertie |
| qpdf | Optionnel, repare les PDF que le moteur de lecture refuse |
| Espace disque | 500 Mo pour le produit, plus le volume de travail |

Windows Server 2012 R2 n'est pas supporte : .NET 10 ne s'y execute pas.

---

## Le compte de service

**C'est la decision d'installation la plus lourde de consequences.**

Le compte systeme local n'a AUCUNE identite reseau. Un service qui tourne sous
ce compte ne peut ni atteindre un partage UNC, ni s'authentifier sur un
serveur FTP interne. L'erreur ne se manifeste qu'au premier export, plusieurs
heures apres l'installation.

Prevoir un compte de domaine dedie, avec :

- **Ouvrir une session en tant que service** dans la strategie locale
- **Modification** sur le repertoire de depot de la GED
- **Lecture** sur la cle privee du certificat, si l'authentification Graph
  passe par un certificat

Le compte n'a pas besoin d'etre administrateur du serveur.

---

## Installation

1. Lancer `ACPollerForMail-x.y.z-setup.exe` en administrateur
2. Accepter la licence, choisir les composants
3. **Renseigner le compte de service** sur la page dediee
4. Cocher LibreOffice et qpdf s'ils ne sont pas deja installes

L'installeur cree les repertoires sous `ProgramData`, genere le jeton de
pilotage, pose les droits NTFS et enregistre le service en demarrage differe
avec redemarrage automatique apres echec.

L'installation de LibreOffice prend plusieurs minutes en silencieux. La page
de progression parait figee pendant ce temps, c'est normal.

---

## Premier parametrage

Le service est installe mais ne fait rien : aucune boite n'est declaree.

1. Lancer **ACPollerForMail** depuis le menu Demarrer
2. L'interface se connecte seule, le jeton lui ayant ete depose
3. Bouton **Nouvelle**, renseigner la source et au moins une cible
4. Bouton **Tester** avant d'enregistrer
5. Cocher **Configuration active**

### La date plancher

**A renseigner a la mise en production** si l'historique n'est pas repris.
Sans elle, le premier demarrage traite tout ce qui dort dans la boite, ce qui
peut representer des milliers de messages.

Onglet General, champ *Ne jamais traiter les messages anterieurs a*.

### Sur un parc de plusieurs boites

Creer d'abord un **gabarit** portant tout ce qui est commun : tenant,
identifiants, cibles, mode de sortie. Chaque boite se reduit alors a son nom,
le gabarit et son adresse.

Le jour ou le secret client change, une seule modification suffit.

---

## Apres le premier demarrage

Les secrets saisis en clair sont chiffres au premier acces. Le service
conserve une copie du fichier d'origine sous `appsettings.json.clear.*.bak`.

**Ces fichiers contiennent les secrets EN CLAIR.** Verifier que le
chiffrement a eu lieu, puis les supprimer :

```
findstr /i "ENC:" "C:\Program Files\ACPollerForMail\appsettings.json"
del "C:\Program Files\ACPollerForMail\appsettings.json.clear.*.bak"
```

---

## Mise a jour

L'installeur d'une nouvelle version :

- arrete le service, remplace les binaires, le redemarre
- **conserve** `appsettings.json` et depose la nouvelle version de reference
  sous `appsettings.json.nouveau`
- **conserve** le journal des messages traites

Comparer `appsettings.json.nouveau` avec la configuration en place pour
reprendre les reglages ajoutes par la nouvelle version.

---

## Desinstallation

L'installeur conserve volontairement :

- `C:\Program Files\ACPollerForMail\appsettings.json`, qui porte les secrets et le
  parametrage
- `C:\ProgramData\ACPollerForMail`, qui porte le journal des messages traites

**Effacer le journal des messages traites provoque un retraitement complet de
l'historique a la prochaine installation**, donc des doublons en GED.

LibreOffice n'est pas desinstalle : il a pu etre installe avant ACPollerForMail ou
servir a autre chose sur ce serveur.

---

## Recette de mise en service

A derouler sur le serveur client avant de declarer le service operationnel.

| Verification | Comment | Resultat attendu |
|---|---|---|
| Service demarre | `Get-Service ACPollerForMail` | `Running` |
| Workers actifs | Journal de demarrage | `N worker(s) demarre(s), 0 ecarte(s)` |
| Source joignable | Bouton **Tester** | Tous les composants en vert |
| Cibles joignables | Bouton **Tester** | Ecriture reelle confirmee |
| LibreOffice | Journal de demarrage | Chemin resolu, pas d'avertissement |
| qpdf | Journal de demarrage | Chemin resolu |
| Acces reseau du compte | Tester une cible UNC | Depot reussi |
| Premier message | Deposer un mail de test | PDF et fichier d'information en GED |
| Idempotence | Attendre un second cycle | Le message n'est pas retraite |
| Redemarrage | `Restart-Service ACPollerForMail` | Reprise sans doublon |

La ligne **acces reseau du compte** est celle qui echoue le plus souvent, et
elle n'echoue que sur un vrai partage : un test local ne la couvre pas.
