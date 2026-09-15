using System.Text;
using ACPoller.Conversion.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ACPoller.Conversion.Converters;

/// <summary>Reglages de mise en page du rendu texte.</summary>
public sealed record TextPdfLayout
{
    /// <summary>Taille de police, en points.</summary>
    public double FontSize { get; init; } = 10;

    /// <summary>Marge, en points (28 points font environ un centimetre).</summary>
    public double Margin { get; init; } = 50;

    /// <summary>Interligne, en multiple de la hauteur de police.</summary>
    public double LineSpacing { get; init; } = 1.25;

    /// <summary>
    /// Nombre maximal de pages produites. Un fichier texte de 50 Mo genererait
    /// sinon des dizaines de milliers de pages et saturerait la GED.
    /// </summary>
    public int MaxPages { get; init; } = 200;
}

/// <summary>
/// Produit un PDF a partir de texte brut, avec retour a la ligne et pagination.
/// </summary>
/// <remarks>
/// Sert a deux usages : la conversion des pieces jointes texte, et le PDF de
/// substitution ecrit quand une conversion echoue. Dans les deux cas, le rendu
/// doit etre sobre et previsible, pas beau.
/// </remarks>
public sealed class TextPdfRenderer(EmbeddedFontResolver fontResolver, TextPdfLayout layout)
{
    /// <summary>Ecrit le texte dans un PDF, en gerant coupures et pagination.</summary>
    /// <param name="text">Texte a rendre.</param>
    /// <param name="outputPath">Chemin du PDF a produire.</param>
    /// <param name="title">Titre du document, optionnel, rendu en gras en tete.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le nombre de pages produites.</returns>
    public async Task<int> RenderAsync(
        string text,
        string outputPath,
        string? title,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        using var document = new PdfDocument();
        document.Info.Creator = "ACPoller";

        if (title is not null)
        {
            document.Info.Title = title;
        }

        var font = new XFont(fontResolver.FamilyName, layout.FontSize);
        var titleFont = new XFont(fontResolver.FamilyName, layout.FontSize + 2, XFontStyleEx.Bold);
        var lineHeight = layout.FontSize * layout.LineSpacing;

        var page = document.AddPage();
        var graphics = XGraphics.FromPdfPage(page);
        var y = layout.Margin;
        var usableWidth = page.Width.Point - (2 * layout.Margin);

        try
        {
            if (title is not null)
            {
                graphics.DrawString(title, titleFont, XBrushes.Black, new XPoint(layout.Margin, y));
                y += lineHeight * 2;
            }

            foreach (var rawLine in SplitLines(text))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var line in Wrap(graphics, rawLine, font, usableWidth))
                {
                    if (y + lineHeight > page.Height.Point - layout.Margin)
                    {
                        if (document.PageCount >= layout.MaxPages)
                        {
                            graphics.DrawString(
                                "[...] Document tronque : nombre maximal de pages atteint.",
                                font, XBrushes.Gray, new XPoint(layout.Margin, y));
                            goto done;
                        }

                        graphics.Dispose();
                        page = document.AddPage();
                        graphics = XGraphics.FromPdfPage(page);
                        y = layout.Margin;
                    }

                    graphics.DrawString(line, font, XBrushes.Black, new XPoint(layout.Margin, y));
                    y += lineHeight;
                }
            }

        done:
            graphics.Dispose();
            graphics = null!;
        }
        finally
        {
            graphics?.Dispose();
        }

        var pageCount = document.PageCount;

        var temporary = outputPath + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await document.SaveAsync(stream, false).ConfigureAwait(false);
        }

        File.Move(temporary, outputPath, overwrite: true);

        return pageCount;
    }

    private static string[] SplitLines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>
    /// Coupe une ligne trop longue. La coupure se fait aux espaces quand c'est
    /// possible, sinon en plein mot : un fichier de donnees sans espace, ce qui
    /// est courant sur les exports plats, doit rester lisible plutot que de
    /// deborder hors de la page.
    /// </summary>
    private static IEnumerable<string> Wrap(XGraphics graphics, string line, XFont font, double maxWidth)
    {
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        // Les tabulations sont converties : PDFsharp ne les interprete pas et
        // les rendrait comme un caractere manquant.
        line = line.Replace("\t", "    ", StringComparison.Ordinal);

        if (graphics.MeasureString(line, font).Width <= maxWidth)
        {
            yield return line;
            yield break;
        }

        var builder = new StringBuilder();

        foreach (var word in line.Split(' '))
        {
            var candidate = builder.Length == 0 ? word : builder + " " + word;

            if (graphics.MeasureString(candidate, font).Width <= maxWidth)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(word);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            // Mot plus long que la ligne : coupure caractere par caractere.
            var remaining = word;

            while (graphics.MeasureString(remaining, font).Width > maxWidth && remaining.Length > 1)
            {
                var take = remaining.Length;

                while (take > 1 && graphics.MeasureString(remaining[..take], font).Width > maxWidth)
                {
                    take--;
                }

                yield return remaining[..take];
                remaining = remaining[take..];
            }

            builder.Append(remaining);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}
