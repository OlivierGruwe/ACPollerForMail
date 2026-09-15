using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace ACPoller.Conversion.Converters;

/// <summary>
/// Convertit les images en PDF, une page par image.
/// </summary>
/// <remarks>
/// Le TIFF multipage passe par ImageSharp : PDFsharp ne sait pas le lire, et
/// c'est pourtant le format de sortie par defaut de beaucoup de scanners et de
/// serveurs de fax. Un TIFF de dix pages traite comme une seule image perdrait
/// neuf pages en silence, ce qui est le pire mode de defaillance possible sur
/// un flux de factures.
/// </remarks>
public sealed class ImageConverter(ILogger<ImageConverter> logger) : IDocumentConverter
{
    private static readonly string[] SupportedExtensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp",
    ];

    /// <inheritdoc />
    public string Name => "Image";

    /// <inheritdoc />
    public int Priority => 200;

    /// <inheritdoc />
    public bool CanHandle(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();

        if (SupportedExtensions.Contains(extension))
        {
            return true;
        }

        return request.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outputPath = Path.Combine(
            request.OutputDirectory,
            SanitizeSegment(request.LogicalPath) + ".pdf");

        var temporaryFrames = new List<string>();

        try
        {
            using var image = await Image.LoadAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);

            using var document = new PdfDocument();
            document.Info.Creator = "ACPoller";

            // Seul le TIFF est un format multipage DOCUMENTAIRE. Les frames
            // d'un GIF ou d'un WebP sont une animation : les rendre toutes
            // produit des dizaines de pages inutiles a partir d'un simple logo
            // de signature.
            var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
            var isMultipageDocument = extension is ".tif" or ".tiff";
            var frameCount = isMultipageDocument ? image.Frames.Count : 1;

            for (var i = 0; i < frameCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // PDFsharp ne consomme pas les images ImageSharp : chaque frame
                // est reecrite en PNG temporaire. Le PNG est sans perte, donc la
                // qualite d'origine est preservee.
                var framePath = Path.Combine(
                    request.OutputDirectory,
                    $"{Guid.NewGuid():N}.png");

                using (var frame = image.Frames.CloneFrame(i))
                {
                    await frame.SaveAsync(framePath, new PngEncoder(), cancellationToken).ConfigureAwait(false);
                }

                temporaryFrames.Add(framePath);

                AddPage(document, framePath, image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution);
            }

            if (document.PageCount == 0)
            {
                return ConversionResult.Failed(
                    Failure.Permanent("Image sans aucune page exploitable."), Name);
            }

            var temporary = outputPath + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await document.SaveAsync(stream, false).ConfigureAwait(false);
            }

            File.Move(temporary, outputPath, overwrite: true);

            if (frameCount > 1)
            {
                logger.LogDebug(
                    "Image multipage convertie : {LogicalPath}, {Count} pages",
                    request.LogicalPath, frameCount);
            }

            return ConversionResult.Success(outputPath, Name, frameCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            // Fichier annonce comme image mais illisible : echec definitif,
            // reessayer ne changera rien.
            return ConversionResult.Failed(
                Failure.Permanent($"Image illisible : {ex.Message}", exception: ex), Name);
        }
        catch (IOException ex)
        {
            // Probleme d'acces disque : potentiellement transitoire.
            return ConversionResult.Failed(
                Failure.Transient($"Acces impossible : {ex.Message}", exception: ex), Name);
        }
        finally
        {
            foreach (var frame in temporaryFrames)
            {
                TryDelete(frame);
            }
        }
    }

    private static void AddPage(PdfDocument document, string imagePath, double dpiX, double dpiY)
    {
        using var xImage = XImage.FromFile(imagePath);

        // Resolution nulle ou absurde sur beaucoup de fax et de scanners :
        // sans ce garde-fou, une page fait plusieurs metres de large.
        var horizontal = dpiX is > 1 and < 2400 ? dpiX : 96;
        var vertical = dpiY is > 1 and < 2400 ? dpiY : 96;

        // Conversion pixels vers points PostScript (72 points par pouce) :
        // c'est ce qui fait qu'un scan a 300 dpi sort au bon format papier
        // plutot qu'en poster.
        var widthPoints = xImage.PixelWidth / horizontal * 72;
        var heightPoints = xImage.PixelHeight / vertical * 72;

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(widthPoints);
        page.Height = XUnit.FromPoint(heightPoints);

        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(xImage, 0, 0, widthPoints, heightPoints);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Suppression de la frame temporaire impossible : {Path}", path);
        }
    }

    private static string SanitizeSegment(string logicalPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. logicalPath.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' ? '_' : c)]);

        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
