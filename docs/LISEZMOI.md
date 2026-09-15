# Production de la documentation

## Contenu

| Fichier | Role |
|---|---|
| `generer.py` | Chaine de production complete |
| `generer-docs.cmd` | Lance les deux langues |
| `style.css` | Mise en forme : couleurs de charte, tableaux, blocs de code |
| `couverture.png` | Couverture du manuel francais, A4 a 150 ppp |
| `cover.png` | Couverture du manuel anglais |

Les sources markdown vivent dans `docs\fr\` et `docs\en\`.

## Prerequis

```
choco install pandoc wkhtmltopdf
pip install pypdf reportlab pillow
```

## Utiliser

```
generer-docs.cmd
```

ou une langue seule :

```
python generer.py fr
```

## Pourquoi trois etapes

**pandoc** convertit le markdown en HTML, **wkhtmltopdf** produit le PDF, puis
un passage **pypdf** ajoute la couverture pleine page et les pieds de page.

Les deux dernieres etapes sont separees parce que wkhtmltopdf ne sait faire ni
l'une ni l'autre correctement :

- ses options de pied de page sont **ignorees** par les builds compiles sans
  Qt modifie, ce qui est le cas de la distribution Windows courante ;
- il n'a pas de notion de fond perdu, donc une couverture pleine page laisse
  un cadre blanc de la largeur des marges.

## Modifier les couvertures

Elles sont generees une fois et versionnees comme des images. Les refaire n'a
d'interet que si la charte change ; le script de generation figure dans
l'historique du depot.

Deux points a retenir si tu les refais :

**L'image de charte porte deja un logo en haut a gauche et une adresse web en
bas a droite.** Le recadrage doit les eviter, sinon un second logo apparait en
filigrane derriere celui de la couverture.

**Le tiers superieur est assombri** pour porter le titre en blanc. Un voile
uniforme eteindrait l'image entiere.
