# ACPoller 2.0 — Manuel

## 1. Ce que fait le produit

ACPoller surveille des boites de messagerie, convertit en PDF tout ce qui y
arrive, et depose le resultat dans une GED ou un systeme de fichiers.

Le cycle, pour chaque message :

1. **Collecte** par IMAP, Microsoft Graph, ou lecture d'un repertoire
2. **Analyse** du message en arbre : corps, pieces jointes, archives, messages
   imbriques
3. **Conversion** de chaque piece en PDF
4. **Assemblage** en un document unique, avec un signet par piece
5. **Fichier d'information** decrivant le message et son contenu
6. **Depot** sur une ou plusieurs cibles
7. **Marquage** du message comme traite

L'ordre de ces deux dernieres etapes n'est pas negociable : le marquage
n'intervient qu'apres confirmation du depot. Un doublon se detecte et se
corrige, une perte silencieuse non.

### Ce qu'il ne fait pas

Il ne lit pas le contenu des documents : ni reconnaissance de caracteres, ni
extraction de donnees. Ces traitements relevent des plugins metier, decrits au
chapitre 9.

Il n'envoie pas de courriel, sauf accuse de reception si un plugin le demande.

---

## 2. Les notions a connaitre

### Configuration

Une **configuration** decrit une boite a surveiller et ce qu'il faut faire de
son contenu : ou collecter, comment convertir, ou deposer. Chaque
configuration donne lieu a un *worker*, qui tourne independamment des autres.

Une configuration en erreur est **ecartee** : les autres continuent. C'est
delibere, et c'est ce qui permet d'exploiter un parc de cent boites sans
qu'une faute de frappe arrete tout.

### Gabarit

Un **gabarit** est une configuration modele dont d'autres heritent. Sur un
parc partageant le meme locataire Microsoft 365, les memes identifiants et les
memes cibles, chaque boite se reduit alors a trois lignes : son nom, le
gabarit, son adresse.

Le jour ou le secret client change, une seule modification suffit.

Une configuration peut surcharger n'importe quelle valeur heritee. Une regle
merite d'etre retenue : **les listes sont remplacees, jamais fusionnees**.
Redeclarer les cibles dans une configuration remplace entierement celles du
gabarit.

### Idempotence

Le service tient un journal des messages deja traites, dans une base locale.
Un message deja livre n'est jamais retraite, meme apres redemarrage, meme si
le message est toujours dans la boite.

Ce journal est **le fichier le plus critique du produit**. Le perdre provoque
le retraitement de tout ce qui reste dans les boites, donc des doublons en
GED.

### Unite de travail

Chaque message en cours de traitement occupe un repertoire de travail, qui
contient le message d'origine, les pieces extraites et les PDF produits. Il
n'est nettoye qu'apres depot confirme.

Une accumulation dans ce repertoire signale donc des depots en echec, pas un
defaut de nettoyage.

### Statut d'une piece

Le fichier d'information porte un statut par piece. Les distinguer est
essentiel : trois d'entre eux signifient que la GED n'a pas le document.

| Statut | Signification |
|---|---|
| `Converted` | PDF fidele produit, present dans le document unifie |
| `PassThrough` | Deja au format PDF, repris tel quel ou repare |
| `PassThroughUnverified` | **Livre a cote, ABSENT du document unifie** |
| `Substituted` | **Non convertie** : une page portant le motif la remplace |
| `Failed` | **Rien n'arrive en GED** pour cette piece |
| `Excluded` | Ecartee volontairement : filtre ou seuil de taille |
| `Container` | Archive ou message imbrique : seuls ses enfants comptent |

---

## 3. Architecture

Le produit comporte deux applications distinctes.

**Le service Windows** fait tout le travail. Il tourne sans session ouverte,
demarre automatiquement, et redemarre seul apres un echec.

**L'interface de supervision** ne fait rien par elle-meme : elle dialogue avec
le service par une API locale. Elle peut etre fermee sans consequence, et
installee sur un poste distinct.

Cette separation a une consequence pratique : l'interface n'ecrit jamais
directement dans les fichiers de configuration. Le service reste seul maitre
de ce qu'il lit et de quand il le relit. Une interface qui ecrirait le fichier
pendant qu'un worker le relit produirait un etat incoherent, sans erreur.

### Les composants externes

| Composant | Role | Sans lui |
|---|---|---|
| LibreOffice | Conversion des pieces bureautiques | Word, Excel et PowerPoint sortent en page de substitution |
| qpdf | Reparation des PDF illisibles | Quelques pourcents des factures ne sont pas fusionnees |

Les deux sont appeles hors processus, jamais lies au produit. Leur absence
degrade le service, elle ne l'empeche pas de tourner.
---

## 4. Installation

### Prerequis

| Element | Exigence |
|---|---|
| Systeme | Windows Server 2016 ou superieur |
| Runtime .NET | Aucun : la publication est autonome |
| Espace disque | 500 Mo, plus le volume de travail |
| Compte de service | Compte de domaine dedie, voir ci-dessous |

Windows Server 2012 R2 n'est pas supporte.

### Le compte de service

**C'est la decision d'installation la plus lourde de consequences.**

Le compte systeme local n'a AUCUNE identite reseau. Un service qui tourne sous
ce compte ne peut ni atteindre un partage UNC, ni s'authentifier sur un
serveur FTP interne. L'erreur ne se manifeste qu'au premier depot, plusieurs
heures apres l'installation.

Le compte de domaine dedie a besoin de :

- **Ouvrir une session en tant que service**, dans la strategie locale
- **Modification** sur le repertoire de depot de la GED
- **Lecture** sur la cle privee du certificat, si l'authentification Graph
  passe par un certificat

Il n'a pas besoin d'etre administrateur du serveur.

### Derouler l'installation

1. Lancer `ACPoller-x.y.z-setup.exe` en administrateur
2. Choisir les composants : LibreOffice et qpdf si le serveur ne les a pas
3. **Renseigner le compte de service** sur la page dediee
4. Laisser l'assistant terminer

L'installeur cree les repertoires, genere le jeton de pilotage, pose les
droits NTFS, et enregistre le service en demarrage differe avec redemarrage
automatique apres echec.

L'installation de LibreOffice prend plusieurs minutes en silencieux. La page
de progression parait figee pendant ce temps.

### Apres le premier demarrage

Les secrets saisis en clair sont chiffres au premier acces, et le service
conserve une copie du fichier d'origine.

**Ces copies contiennent les secrets EN CLAIR.** Verifier que le chiffrement a
eu lieu, puis les supprimer :

```
findstr /i "ENC:" "C:\Program Files\ACPoller\appsettings.json"
del "C:\Program Files\ACPoller\appsettings.json.clear.*.bak"
```

Le service les purge de lui-meme passe vingt-quatre heures, mais autant ne pas
attendre.

---

## 5. L'interface

### Le bandeau d'etat

Premiere chose a regarder. Un voyant vert et le nombre de workers actifs
signifient que le service tourne. Un voyant rouge distingue deux cas par son
message : service injoignable, ou jeton refuse.

### Les deux listes de gauche

**Configurations** liste les boites surveillees, avec leur protocole et le
nombre d'anomalies detectees. Une anomalie bloquante ecarte la configuration :
elle apparait en rouge et son worker ne tourne pas.

**Gabarits** liste les modeles, avec le nombre de configurations qui en
heritent. Ce nombre est la **portee** d'une modification : le voir avant
d'ouvrir un gabarit evite de modifier cinquante boites en croyant en modifier
une.

### Les deux onglets de droite

**Supervision** montre le detail de la configuration selectionnee : ses
anomalies, le resultat du dernier test, l'etat de tous les workers.

**Tableau de bord** montre l'activite des vingt-quatre dernieres heures.

### Le tableau de bord

Quatre indicateurs suffisent a savoir si tout va bien.

| Indicateur | Ce qu'il dit |
|---|---|
| Taux de succes | Est-ce que ca tourne normalement ? |
| 95e centile de duree | Des messages lents saturent-ils les traitements ? |
| Limitations Graph | Suis-je pres du quota du locataire ? |
| Disque libre | Combien de temps avant l'arret ? |

La ventilation par configuration, en bas, trie les boites par nombre
d'echecs : la premiere ligne designe celle qui pose probleme.

Le 95e centile merite une explication. Sur des durees de traitement, la
moyenne masque les cas lents, et ce sont eux qui occupent les traitements
simultanes pendant que les autres boites attendent. Un ecart important entre
la moyenne et ce centile signale une poignee de messages qui monopolisent le
service.

### Les boutons

| Bouton | Effet |
|---|---|
| Nouvelle, Modifier, Supprimer | Agit sur la configuration selectionnee |
| Tester | Verifie la source et toutes les cibles, sans traiter de message |
| Redemarrer | Relance le worker de la configuration, sans toucher aux autres |
| Rafraichir | Force une mise a jour, aussi disponible par F5 |
| Maintenance | Declenche une passe immediate de nettoyage |
| Service | Reglages du serveur, qui demandent un redemarrage |
| Connexion | Adresse et jeton de l'interface elle-meme |
| Aide | Reference des jetons, statuts et diagnostic, aussi par F1 |

**Tester** est le bouton a utiliser avant tout enregistrement. Il ecrit
reellement sur les cibles de fichiers, pour verifier les droits et pas
seulement l'existence du chemin.
---

## 6. Configurer une boite

Chaque onglet de l'editeur couvre une etape du traitement.

### Onglet General

**Nom** : identifiant unique, utilise comme cle dans les journaux et comme nom
du repertoire de travail. Le changer revient a creer une nouvelle
configuration, et le journal des messages traites repart de zero.

**Gabarit** : modele dont heriter. Vide pour une configuration autonome.

**Periode entre deux cycles** : delai entre la fin d'un cycle et le debut du
suivant. Une valeur trop courte sur un grand parc multiplie les appels sans
rien gagner, la collecte etant deja bornee par la concurrence globale.

**Decalage aleatoire au demarrage** : sans lui, toutes les boites frappent le
serveur a la meme seconde au demarrage du service, ce qui declenche la
limitation avant meme le premier message.

**Rattrapage au premier cycle** : ne s'applique qu'au tout premier cycle,
quand rien n'a encore ete traite pour cette boite. Une valeur trop faible fait
croire que rien n'arrive alors que le service fonctionne, et rien ne le
signale.

**Date plancher** : aucun message anterieur ne sera jamais traite.
Contrairement au rattrapage, elle ne glisse pas. **A renseigner a la mise en
production** quand l'historique n'est pas repris : sans elle, le premier
demarrage traite tout ce qui dort dans la boite.

### Onglet Protocole

Le choix du protocole affiche les champs correspondants.

**graph** pour Microsoft 365. L'authentification par **certificat** est
preferable au secret client, qui expire toujours un vendredi soir.

**imap** pour une messagerie classique. Le port 993 correspond au SSL
implicite, le 143 a STARTTLS.

**folder** pour lire des fichiers `.eml` sur disque. Ce mode sert au rejeu
d'un message litigieux et a la recette, sans boite ni reseau.

**Dossier surveille** : designe par son nom canonique et non par son libelle.
Sur une boite en francais, le libelle est *Boite de reception*, et toute
recherche par nom echouerait.

**Apres traitement** : applique une fois le depot confirme. `None` laisse le
message intact, ce qui permet de rejouer autant qu'on veut pendant une
recette. En production, `MarkAsRead` ou `Move` evitent de relister
indefiniment les memes messages.

**Limitation de debit**, en Graph uniquement. Ces bornes sont **partagees par
toutes les boites du meme locataire**. L'espacement compte autant que la
concurrence : sans lui, huit appels partent a la meme milliseconde et
declenchent la limitation avant d'atteindre la limite.

### Onglet Conversion

**Delai maximal par piece** : depasse, le convertisseur est **tue** et non
attendu. Un LibreOffice bloque sur un document corrompu ne se debloque jamais.

**Piece non convertible** : trois politiques.

| Politique | Effet |
|---|---|
| `Substitute` | Une page portant le nom et le motif remplace la piece |
| `Skip` | La piece est omise, sans trace dans le PDF |
| `RejectMessage` | Tout le message est rejete, aucune sortie partielle |

`Substitute` est le defaut recommande : la GED voit qu'il manque quelque
chose, ce qui declenche une relance fournisseur. Avec `Skip`, le manque passe
inapercu.

**Seuil d'image incorporee** : en dessous, une image referencee par le corps
HTML est ecartee. Sans ce filtre, chaque message produit trois pages de logos
de signature.

**Bornes d'extraction** : profondeur, nombre de pieces, volume decompresse,
taille d'une entree. Elles protegent d'une archive forgee, et les fichiers
traites arrivent par courriel, donc de n'importe qui. Les augmenter sans
raison expose le service.

**Extensions ecartees** : sert a retirer les signatures electroniques et les
invitations, qui n'ont pas vocation a partir en GED.

### Onglet Format de sortie

**Mode de production des PDF** :

| Mode | Resultat |
|---|---|
| `Merged` | Un seul PDF par message, corps puis pieces, avec signets |
| `PerAttachment` | Un PDF par piece |
| `Both` | Les deux |

**Format du fichier d'information** : `json`, `xml`, `csv`, ou `xslt`.

`xslt` applique une feuille de transformation au XML canonique. C'est ce qui
permet de produire la structure exacte attendue par une GED, y compris du
texte plat, sans livraison de code. Pour mettre au point une feuille : passer
le format en `xml`, regarder la sortie reelle, ecrire la feuille dessus, puis
revenir en `xslt`.

**Gabarit de nommage** : voir l'annexe A pour la liste des jetons. En mode
`PerAttachment`, le jeton `{index}` est **obligatoire** sous peine
d'ecrasement entre pieces.

**Ecriture atomique** : le fichier est ecrit sous une extension temporaire
puis renomme. Sans cela, une GED qui scrute le repertoire peut ramasser un PDF
en cours d'ecriture, et l'erreur est indetectable de son cote.

**Tout ou rien entre les cibles** : si une cible echoue, les depots deja faits
sur les autres sont annules. Une GED qui recoit un message qu'une seconde n'a
pas recu produit un ecart que personne ne detecte avant l'audit.

**Fichier temoin** : ecrit en dernier, il indique a la GED que le lot est
complet. Sans lui, elle peut prendre le PDF sans son fichier d'information, ou
l'inverse.

### Onglet Traitements

Liste des traitements metier apportes par les plugins installes, avec les
etapes du pipeline auxquelles ils interviennent.

Un traitement encadre en rouge est declare dans la configuration mais son
plugin n'est plus charge : il ne s'executera pas, et le service ecartera la
configuration au prochain demarrage.

Les plugins refuses apparaissent en dessous avec leur motif. Sans cette liste,
on chercherait un traitement qui existe bien dans le repertoire mais dont le
plugin n'a pas ete charge.

### Onglet Cibles

Une configuration peut deposer sur plusieurs cibles. Chaque cible porte un
**nom**, repris dans les journaux : sans nom distinct, un echec sur l'une des
trois cibles d'un flux est illisible.

Les champs proposes dependent du transport, mais le tableau du bas montre
**tous** les reglages, y compris ceux qu'aucun champ ne couvre. C'est ce qui
permet de configurer une DLL d'export cliente.

### Onglet Champs

Champs ajoutes au fichier d'information. **L'ordre fait foi** : c'est celui
des colonnes du CSV, et le modifier peut casser une integration en place.

Voir l'annexe B pour les sources disponibles et l'ordre de resolution.
---

## 7. Les cibles d'export

### Systeme de fichiers

Le transport le plus courant : un repertoire local ou un partage UNC.

Le compte de service doit avoir le droit de **modification** sur ce
repertoire. Un partage UNC exige en outre que le service tourne sous un compte
de domaine, le compte systeme local n'ayant aucune identite reseau.

Le bouton **Tester** ecrit reellement un fichier temoin, puis le supprime : il
verifie les droits, pas seulement l'existence du chemin.

### FTP et FTPS

Le chiffrement explicite sur le port 21 est le defaut, l'implicite utilise le
port 990.

**Accepter tout certificat** supprime toute protection contre l'interception :
un tiers peut se placer entre le service et le serveur sans etre detecte. A
n'activer que sur un certificat auto-signe connu, et a documenter. Le service
le rappelle a chaque session dans son journal.

### S3 et compatibles

Pour AWS, la region suffit. Pour MinIO, Garage ou Ceph, renseigner le **point
d'acces personnalise** : le style chemin et le mode de somme de controle
compatible sont alors actives automatiquement. Sans eux, ces implementations
refusent les depots avec une erreur qui ne designe pas la cause.

Le **chiffrement au repos** doit rester vide si le stockage ne le configure
pas : le declarer ferait refuser chaque depot.

Les cles peuvent etre laissees vides pour utiliser la chaine de resolution AWS
habituelle, variables d'environnement ou role d'instance.

---

## 8. Champs personnalises et transformation

### Pourquoi

Aucun format fige ne convient a toutes les GED : l'une veut un code societe en
dur, l'autre la date du jour dans son propre format, une troisieme un en-tete
pose par son relais. Les figer dans le code imposerait une livraison par
client.

### Les cinq sources

| Source | Valeur |
|---|---|
| `Fixed` | Litterale, identique pour tous les messages |
| `Token` | Gabarit a jetons, resolu comme le nommage |
| `Header` | En-tete MIME, nom insensible a la casse |
| `Property` | Valeur deposee par un traitement metier |
| `Environment` | Variable d'environnement du serveur |

### L'ordre de resolution

Valeur d'origine, puis format de date, puis table de traduction, puis valeur
par defaut si le resultat est vide, puis longueur fixe.

La **table de traduction** convertit un libelle en code attendu par la GED.
Une valeur absente de la table passe inchangee.

La **longueur fixe** sert aux GED qui lisent du positionnel. Une valeur trop
longue est tronquee : deborder decalerait toutes les colonnes suivantes, ce
qui est pire que perdre la fin d'un libelle.

Un champ non resolu **ne fait jamais echouer le traitement**. Il produit sa
valeur par defaut et un avertissement. C'est le document qui compte, pas la
completude de son index.

### La transformation XSLT

Le service produit toujours le meme XML canonique. Une feuille de style propre
au client le transforme en ce qu'attend sa GED.

Le format de sortie n'est pas forcement du XML : une feuille declarant
`xsl:output method="text"` produit du plat, du CSV ou du largeur fixe.

Par securite, **les scripts et la fonction `document()` sont desactives**. Une
feuille est un fichier de configuration, et autoriser l'execution de code
depuis un fichier de configuration reviendrait a donner les droits du compte
de service a quiconque peut ecrire dans le repertoire.

---

## 9. Traitements metier

Un plugin est une DLL deposee dans le repertoire de plugins. Le service la
lit par metadonnees sans executer son code, verifie sa compatibilite, puis la
charge dans un contexte isole.

Un plugin peut apporter :

- des **traitements** intervenant a differentes etapes du pipeline
- des **cibles d'export** propres au client
- des **referentiels** consultes par les traitements

### Les etapes d'intervention

| Etape | Moment |
|---|---|
| `AfterParse` | Apres analyse du message, avant conversion |
| `BeforeConvert` | Avant chaque conversion de piece |
| `AfterConvert` | Apres conversion, avant assemblage |
| `BeforeExport` | Avant depot, dernier moment pour rejeter |
| `AfterExport` | Apres depot confirme |

Un traitement peut **rejeter** un message ou l'**ecarter**. Le rejet signale
une anomalie, l'ecartement un message qui ne concerne pas ce flux. Les deux
apparaissent separement dans le tableau de bord.

Un traitement depose ses resultats dans un sac de proprietes, que les champs
personnalises de source `Property` peuvent reprendre. C'est par ce canal qu'un
code fournisseur extrait du sujet arrive dans le fichier d'information.

### Version de contrat

Chaque plugin declare la version de contrat qu'il attend. Un plugin trop
ancien est **refuse et non charge**, avec son motif visible dans l'interface.
Le charger produirait des erreurs a l'execution, bien plus difficiles a
diagnostiquer qu'un refus explicite au demarrage.

---

## 10. Reglages de service

Bouton **Service**. Ces reglages ne sont **pas rechargeables a chaud** : toute
modification exige un redemarrage complet, pendant lequel la capture est
interrompue.

Apres enregistrement, un bandeau orange apparait et une notification s'affiche
dans la zone de notification. Le detail liste les reglages en attente et la
raison de chacun.

### Ce qu'ils couvrent

| Onglet | Reglages |
|---|---|
| Service | Repertoire de travail, concurrence globale, plugins, retention |
| Outils | LibreOffice, profils, qpdf, polices |
| Maintenance | Periode, quarantaine, seuil d'espace disque |
| Pilotage | Activation et port de l'API, jeton, base de metriques |

### Trois pieges

**Changer le repertoire de travail perd le journal d'idempotence.** Les
messages encore presents dans les boites seront retraites.

**Augmenter la concurrence globale au-dela du nombre de profils LibreOffice ne
sert a rien** : la conversion devient le goulot.

**Changer le jeton invalide tous les postes d'exploitation**, y compris celui
depuis lequel on le change.

### Redemarrer

L'interface ne redemarre pas le service elle-meme : elle dialogue avec lui par
son API, et un service qui s'arrete sur ordre de son propre client ne pourrait
pas confirmer l'ordre execute.

```
Restart-Service ACPoller
```
---

## 11. Exploitation quotidienne

### Verifier que tout va bien

Ouvrir l'interface, onglet Tableau de bord. Si le taux de succes est a 100 %,
qu'aucune limitation Graph n'apparait et que le disque a de la marge, il n'y a
rien a faire.

Sans interface :

```
curl -H "X-ACPoller-Token: <jeton>" http://127.0.0.1:5199/api/status
```

### Suspendre une boite

Decocher **Configuration active**, enregistrer. Le worker s'arrete, le
parametrage est conserve.

### Changer un mot de passe

Onglet Protocole, saisir la nouvelle valeur, enregistrer. Le secret est
chiffre par le service au premier acces.

**Laisser le champ vide conserve la valeur actuelle.** L'interface ne recoit
jamais les secrets, elle ne peut donc pas les renvoyer.

### Ajouter une boite sur un parc existant

Utiliser un gabarit. La nouvelle configuration se reduit a son nom, le
gabarit et l'adresse.

### Rejouer un message

Le message d'origine est conserve dans son repertoire de travail, sous le nom
`message.eml`, et dans la quarantaine pour les unites abandonnees.

1. Copier le `.eml` dans un repertoire
2. Creer une configuration en protocole **folder** pointant dessus
3. Lui donner des cibles de test
4. Demarrer

Le message est rejoue a l'identique, sans toucher a la boite ni au reseau.

Attention : le journal des messages traites fonctionne aussi en mode folder.
Pour rejouer deux fois le meme message, changer le nom de la configuration.

### Reprendre apres un arret prolonge

Apres plusieurs jours d'arret, les boites contiennent l'accumulation.

1. Verifier l'espace disque avant de demarrer
2. Reduire temporairement le plafond de messages par cycle
3. Surveiller les limitations Graph pendant la premiere heure
4. Remettre les valeurs nominales une fois le retard resorbe

---

## 12. Diagnostic

### Un message revient a chaque cycle

Verifier d'abord que **le depot reussit** : le marquage n'intervient qu'apres
depot confirme.

Si le depot reussit, regarder la disposition : en `None`, le message reste non
lu et sera relu a chaque cycle.

### Le PDF unifie est vide ou incomplet

Ouvrir le fichier d'information et regarder le statut de chaque piece. Trois
statuts signifient que la GED n'a pas le document : `PassThroughUnverified`,
`Substituted` et `Failed`.

Si toutes les pieces bureautiques echouent, LibreOffice est absent ou mal
configure.

### Rien n'est collecte, sans erreur

Le service tourne et ne trouve rien. Regarder, dans cet ordre :

1. **Rattrapage au premier cycle** : une valeur faible exclut les messages
   anciens
2. **Ne prendre que les non lus** : les messages ouverts dans le webmail sont
   marques comme lus
3. **Date plancher** : posterieure aux messages presents
4. **Dossier surveille** : les messages peuvent etre dans un sous-dossier

Passer le niveau de journalisation en `Debug` rend le cycle bavard :

```
setx ACPOLLER_LOGLEVEL Debug /M
Restart-Service ACPoller
```

### Le disque se remplit

Le repertoire de travail n'est nettoye qu'apres depot confirme. Une
accumulation signale des depots en echec.

Le bouton **Maintenance** declenche une passe immediate : quarantaine des
unites inachevees de plus de sept jours, suppression des repertoires
orphelins.

### Le service ne demarre pas

```
findstr /i "worker(s) demarre" logs\acpoller-*.log
```

La ligne `N worker(s) demarre(s), M ecarte(s)` donne le compte. Si `M` n'est
pas nul, les motifs sont juste au-dessus, en `Error`.

### L'interface affiche « Jeton refuse »

Le jeton de l'interface ne correspond pas a celui du service. Ouvrir
**Connexion**, bouton **Tester la connexion** : le message distingue un
service injoignable d'un jeton refuse.

Le jeton en clair est dans
`C:\ProgramData\ACPoller\ui-settings.default.json`.

### Retrouver le traitement d'un message

Le fichier d'information porte un `CorrelationId`, present dans tous les
journaux du traitement :

```
findstr /i "<CorrelationId>" logs\acpoller-*.log
```

---

## 13. Ce qu'il ne faut pas faire

**Ne pas supprimer le journal des messages traites** pour repartir proprement.
Tout ce qui reste dans les boites sera retraite.

**Ne pas modifier la configuration a la main pendant que le service tourne.**
L'interface detecte les modifications concurrentes et refuse d'ecraser, mais
l'inverse n'est pas vrai.

**Ne pas supprimer les sauvegardes `.bak` sans les lire.** Ce sont les copies
automatiques avant chaque modification, et le seul moyen de revenir en
arriere. En revanche, les fichiers `.clear.*.bak` contiennent les secrets en
clair et doivent etre supprimes une fois le chiffrement verifie.

**Ne pas augmenter les bornes d'extraction sans raison.** Elles protegent
d'une archive forgee, et les fichiers traites arrivent de n'importe qui.

**Ne pas oublier la date plancher a la mise en production.** Sans elle, le
premier demarrage traite tout l'historique dormant dans la boite.
---

## Annexe A — Jetons de nommage

Utilisables dans le gabarit de nommage et dans les champs de source `Token`.
Un format peut suivre deux points, par exemple `{received:yyyyMMdd}`.

| Jeton | Valeur |
|---|---|
| `{date}` | Date de **traitement**, en heure locale |
| `{time}` | Heure de traitement |
| `{datetime}` | Date et heure de traitement |
| `{received}` | Date de **reception** du message |
| `{guid}` | Identifiant aleatoire, garantit l'unicite |
| `{mailbox}` | Partie locale de l'adresse de la boite |
| `{config}` | Nom de la configuration |
| `{subject}` | Sujet du message, tronque et assaini |
| `{from}` | Partie locale de l'adresse de l'expediteur |
| `{index}` | Numero de la piece, **obligatoire** en mode PerAttachment |
| `{name}` | Nom du fichier sans extension |
| `{ext}` | Extension de la piece |
| `{node}` | Chemin logique complet |

`{date}` change si le message est rejoue le lendemain, `{received}` non. Pour
un nommage qui doit rester reproductible apres incident, preferer `{received}`.

Tous les caracteres interdits dans un nom de fichier Windows sont remplaces,
separateurs de chemin compris : un sujet contenant une barre oblique ne peut
pas creer de sous-repertoire sur la cible.

---

## Annexe B — Sortie des champs personnalises

Le meme contenu sort differemment selon le format.

**json** : un objet `fields`, noms en cles.

**xml** : un bloc `Fields`, le nom du champ etant un **attribut** et non un nom
d'element. Un nom saisi par un exploitant peut contenir un espace ou commencer
par un chiffre, ce qui produirait un XML invalide en nom d'element.

**csv** : des colonnes ajoutees **apres** les colonnes fixes, repetees sur
chaque ligne. Ajouter un champ ne decale donc jamais les colonnes existantes.

**xslt** : a la disposition de la feuille de transformation.

---

## Annexe C — Emplacements

| Chemin | Contenu | Critique |
|---|---|---|
| `C:\Program Files\ACPoller\appsettings.json` | Configuration, secrets chiffres | Oui |
| `C:\Program Files\ACPoller\logs\` | Journaux | Non |
| `C:\Program Files\ACPoller\docs\` | Ce manuel | Non |
| `C:\ProgramData\ACPoller\work\` | Unites de travail en cours | Non |
| `C:\ProgramData\ACPoller\work\state\processed.db` | Journal des messages traites | **Critique** |
| `C:\ProgramData\ACPoller\work\_quarantaine\` | Unites abandonnees, 30 jours | Non |
| `C:\ProgramData\ACPoller\ui-settings.default.json` | Adresse et jeton de l'interface | Non |
| `%APPDATA%\ACPoller\ui-settings.json` | Reglages de l'exploitant | Non |

### Sauvegarde

Deux elements doivent figurer dans la sauvegarde du serveur :

- `C:\ProgramData\ACPoller\work\state\processed.db`
- `C:\Program Files\ACPoller\appsettings.json`

Le premier est le seul dont la perte a une consequence irreversible. Le
second se reconstruit, mais il faudrait ressaisir tous les secrets.

---

## Annexe D — API de pilotage

Ecoute sur la boucle locale uniquement. Toute requete porte l'en-tete
`X-ACPoller-Token`.

| Methode | Route | Effet |
|---|---|---|
| GET | `/api/status` | Etat du service et des workers |
| GET | `/api/dashboard` | Indicateurs des 24 dernieres heures |
| GET | `/api/configurations` | Configurations et leurs anomalies |
| GET | `/api/configurations/{nom}/raw` | JSON d'une configuration, secrets masques |
| PUT | `/api/configurations/{nom}` | Cree ou remplace une configuration |
| DELETE | `/api/configurations/{nom}` | Supprime une configuration |
| POST | `/api/configurations/{nom}/test` | Teste source et cibles |
| POST | `/api/workers/{nom}/restart` | Redemarre un worker |
| GET | `/api/templates` | Gabarits et leur portee |
| GET | `/api/processors` | Traitements disponibles, plugins refuses |
| GET | `/api/settings` | Reglages de service, secrets masques |
| PUT | `/api/settings` | Modifie les reglages de service |
| POST | `/api/maintenance/run` | Declenche une passe de maintenance |

Les secrets ne sortent **jamais** de l'API : ils sont remplaces par une chaine
vide. A l'ecriture, un champ vide signifie inchange.

Chaque lecture renvoie une **empreinte** du fichier de configuration, que
l'ecriture reclame. Si le fichier a change entre-temps, la modification est
refusee plutot que d'ecraser celle d'un autre.

---

## Annexe E — Recette de mise en service

A derouler sur le serveur client avant de declarer le service operationnel.

| Verification | Comment | Attendu |
|---|---|---|
| Service demarre | `Get-Service ACPoller` | `Running` |
| Workers actifs | Journal de demarrage | `N demarre(s), 0 ecarte(s)` |
| Source joignable | Bouton **Tester** | Tous les composants en vert |
| Cibles joignables | Bouton **Tester** | Ecriture reelle confirmee |
| LibreOffice | Journal de demarrage | Chemin resolu |
| qpdf | Journal de demarrage | Chemin resolu |
| Acces reseau du compte | Tester une cible UNC | Depot reussi |
| Premier message | Deposer un message de test | PDF et fichier d'information deposes |
| Idempotence | Attendre un second cycle | Le message n'est pas retraite |
| Redemarrage | `Restart-Service ACPoller` | Reprise sans doublon |
| Date plancher | Onglet General | Renseignee si l'historique n'est pas repris |
| Secrets chiffres | `findstr ENC: appsettings.json` | Au moins une occurrence |
| Sauvegardes en clair | `dir *.clear.*.bak` | Supprimees |

La ligne **acces reseau du compte** est celle qui echoue le plus souvent, et
elle n'echoue que sur un vrai partage : un test local ne la couvre pas.
