"""
Produit les PDF de documentation depuis les sources markdown.

Trois etapes : pandoc convertit le markdown en HTML, wkhtmltopdf produit le
PDF, puis un passage pypdf ajoute la couverture pleine page et les pieds de
page.

Les deux dernieres etapes sont separees parce que wkhtmltopdf ne sait faire ni
l'une ni l'autre correctement : ses options de pied de page sont ignorees par
les builds sans Qt modifie, et il n'a pas de notion de fond perdu, ce qui
laisse un cadre blanc autour d'une couverture pleine page.

Usage : python generer.py fr|en
"""
import io
import re
import subprocess
import sys
from pathlib import Path

from pypdf import PdfReader, PdfWriter
from reportlab.lib.colors import HexColor
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

RACINE = Path(__file__).parent

# Les documents, par langue : source, titre du pied de page, sous-titre
# d'en-tete, et couverture pleine page pour le seul manuel.
DOCUMENTS = {
    "fr": [
        ("MANUEL", "Manuel", None, "couverture.png", "Sommaire"),
        ("EXPLOITATION", "Guide d'exploitation",
         "Supervision, incidents courants et interventions", None, None),
        ("INSTALLATION", "Guide d'installation",
         "Prerequis, mise en service et recette", None, None),
    ],
    "en": [
        ("MANUAL", "Manual", None, "cover.png", "Contents"),
        ("OPERATIONS", "Operations guide",
         "Monitoring, common incidents and routine tasks", None, None),
        ("SETUP", "Installation guide",
         "Prerequisites, commissioning and checklist", None, None),
    ],
}

STYLE_SOMMAIRE = """<style>
.sommaire-titre {{ font-size: 18pt; font-weight: 300; color: #5B667A;
  border-bottom: 3px solid #00A6DE; padding-bottom: 8px; margin-bottom: 14px; }}
#TOC ul {{ list-style: none; padding-left: 0; }}
#TOC ul ul {{ padding-left: 16px; font-size: 9pt; }}
#TOC li {{ margin-bottom: 4px; }}
#TOC a {{ text-decoration: none; color: #1c1c1c; }}
#TOC {{ page-break-after: always; }}
/* Chaque chapitre commence sur une page neuve : un manuel se consulte par
   chapitre, pas en continu. */
h2 {{ page-break-before: always; }}
h2:first-of-type {{ page-break-before: avoid; }}
</style>
</head>"""

STYLE_ENTETE = """<style>
.couverture {{ margin-bottom: 10px; }}
.couverture .produit {{ font-size: 9pt; color: #00A6DE; font-weight: 600;
  letter-spacing: 0.5px; }}
.couverture .soustitre {{ font-size: 9pt; color: #7a828c; }}
</style>
</head>"""


def pied_de_page(index: int, total: int, titre: str):
    """Produit une page ne contenant que le pied, a fusionner par-dessus."""
    tampon = io.BytesIO()
    c = canvas.Canvas(tampon, pagesize=A4)

    # Filet fin : sans lui, le numero flotte et donne l'impression d'un
    # debordement de contenu.
    c.setStrokeColor(HexColor("#d8dce2"))
    c.setLineWidth(0.5)
    c.line(45, 36, A4[0] - 45, 36)

    c.setFont("Helvetica", 7)
    c.setFillColor(HexColor("#7a828c"))
    c.drawString(45, 26, f"ACPoller 2.0  —  {titre}")
    c.drawRightString(A4[0] - 45, 26, f"{index} / {total}")
    c.save()

    tampon.seek(0)
    return PdfReader(tampon).pages[0]


def page_couverture(image: Path):
    """Couverture pleine page, sans marge."""
    tampon = io.BytesIO()
    c = canvas.Canvas(tampon, pagesize=A4)
    c.drawImage(ImageReader(str(image)), 0, 0, width=A4[0], height=A4[1])
    c.save()

    tampon.seek(0)
    return PdfReader(tampon).pages[0]


def construire(langue: str, nom: str, titre: str, soustitre, couverture, sommaire):
    dossier = RACINE.parent / langue
    source = dossier / f"{nom}.md"

    if not source.exists():
        print(f"  {source} absent, ignore")
        return

    html = RACINE / f"_{nom}.html"
    corps = RACINE / f"_{nom}-corps.pdf"

    commande = [
        "pandoc", str(source), "-f", "markdown", "-t", "html5", "-s",
        "--metadata", "title= ", "-c", "style.css", "-o", str(html),
    ]

    if sommaire:
        commande += ["--toc", "--toc-depth=2"]

    subprocess.run(commande, check=True, cwd=RACINE)

    texte = html.read_text(encoding="utf-8")

    # Le bloc de titre injecte par pandoc fait doublon avec le titre du
    # markdown lui-meme.
    texte = re.sub(r'<header id="title-block-header">.*?</header>\n?', "", texte, flags=re.S)

    if sommaire:
        texte = texte.replace("<body>", f'<body>\n<div class="sommaire-titre">{sommaire}</div>\n', 1)
        texte = texte.replace("</head>", STYLE_SOMMAIRE.format())
    elif soustitre:
        entete = (f'<div class="couverture">\n'
                  f'  <div class="produit">ACPoller 2.0</div>\n'
                  f'  <div class="soustitre">{soustitre}</div>\n'
                  f'</div>\n')
        texte = texte.replace("<body>", "<body>\n" + entete, 1)
        texte = texte.replace("</head>", STYLE_ENTETE.format())

    html.write_text(texte, encoding="utf-8")

    subprocess.run([
        "wkhtmltopdf", "--enable-local-file-access",
        "--margin-top", "16mm", "--margin-bottom", "16mm",
        "--margin-left", "16mm", "--margin-right", "16mm",
        "--quiet", str(html), str(corps),
    ], check=True, cwd=RACINE)

    lecteur = PdfReader(corps)
    ecrivain = PdfWriter()

    decalage = 0

    if couverture:
        ecrivain.add_page(page_couverture(RACINE / couverture))
        decalage = 1

    total = len(lecteur.pages) + decalage

    for index, page in enumerate(lecteur.pages, start=1 + decalage):
        page.merge_page(pied_de_page(index, total, titre))
        ecrivain.add_page(page)

    ecrivain.add_metadata({
        "/Title": f"ACPoller 2.0 — {titre}",
        "/Author": "Arondor",
    })

    with open(dossier / f"{nom}.pdf", "wb") as sortie:
        ecrivain.write(sortie)

    html.unlink()
    corps.unlink()

    print(f"  {nom}.pdf : {total} pages")


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in DOCUMENTS:
        print("Usage : python generer.py fr|en")
        return 1

    langue = sys.argv[1]

    for nom, titre, soustitre, couverture, sommaire in DOCUMENTS[langue]:
        construire(langue, nom, titre, soustitre, couverture, sommaire)

    return 0


if __name__ == "__main__":
    sys.exit(main())
