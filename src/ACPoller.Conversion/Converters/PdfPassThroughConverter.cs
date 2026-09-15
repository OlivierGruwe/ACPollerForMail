using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf.IO;

namespace ACPoller.Conversion.Converters;

/// <summary>
/// Traite les pieces deja au format PDF : validation, reparation, ou copie
/// telle quelle en dernier recours.
/// </summary>
/// <remarks>
/// Trois issues possibles, dans cet ordre :
///
/// 1. Le PDF s'ouvre : il est copie et fusionnable.
/// 2. Il ne s'ouvre pas : qpdf tente une reparation, et le resultat est copie.
/// 3. La reparation echoue : le fichier d'ORIGINE est copie tel quel, avec un
///    statut degrade.
///
/// Le troisieme cas est le point important. Un PDF que PDFsharp refuse est
/// souvent parfaitement valide pour Acrobat et pour la GED : c'est notre
/// lecteur qui est strict, pas le document qui est casse. Substituer une page
/// d'erreur a une facture reelle detruirait de l'information que personne ne
/// reclamerait avant la relance fournisseur.
///
/// Le prix a payer est explicite : la piece est absente du PDF unifie, puisque
/// nous ne savons pas la lire pour la fusionner. Le fichier d'information le
/// dit, et la GED recoit le vrai document a cote.
///
/// Priorite la plus haute de la chaine : un PDF ne doit jamais partir chez
/// LibreOffice, qui le reconvertirait en degradant sa qualite.
/// </remarks>
public sealed class PdfPassThroughConverter(
    IPdfAssembler assembler,
    ILogger<PdfPassThroughConverter> logger) : IDocumentConverter
{
    /// <inheritdoc />
    public string Name => "PdfPassThrough";

    /// <inheritdoc />
    public int Priority => 1000;

    /// <inheritdoc />
    public bool CanHandle(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Path.GetExtension(request.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.ContentType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destination = Path.Combine(
            request.OutputDirectory,
            SanitizeSegment(request.LogicalPath) + ".pdf");

        // 1. Le cas nominal.
        var pageCount = TryReadPageCount(request.SourcePath);

        if (pageCount is not null)
        {
            File.Copy(request.SourcePath, destination, overwrite: true);
            return ConversionResult.PassThrough(destination, Name, pageCount);
        }

        // 2. Reparation.
        logger.LogInformation(
            "PDF non lisible par le moteur, tentative de reparation : {LogicalPath}",
            request.LogicalPath);

        var repaired = await assembler.RepairAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);

        if (repaired is not null && TryReadPageCount(repaired) is { } repairedPages)
        {
            File.Copy(repaired, destination, overwrite: true);

            logger.LogInformation(
                "PDF repare et repris : {LogicalPath}, {Pages} pages",
                request.LogicalPath, repairedPages);

            return ConversionResult.PassThrough(destination, Name, repairedPages);
        }

        // 3. Copie telle quelle. Le document part en sortie, mais il ne pourra
        // pas etre fusionne dans le PDF unifie.
        File.Copy(request.SourcePath, destination, overwrite: true);

        logger.LogWarning(
            "PDF copie sans validation : {LogicalPath}. Absent du PDF unifie, "
            + "le document d'origine est livre tel quel.",
            request.LogicalPath);

        return new ConversionResult
        {
            Status = NodeStatus.PassThroughUnverified,
            PdfPath = destination,
            ConverterName = Name,
            Failure = Failure.Permanent(
                "PDF illisible par le moteur de lecture, meme apres reparation. "
                + "Le document d'origine est livre tel quel, mais il n'a pas pu etre "
                + "fusionne dans le PDF unifie."),
        };
    }

    /// <summary>
    /// Ouvre le PDF en mode import pour verifier qu'il est structurellement
    /// exploitable. Retourne null si l'ouverture echoue.
    /// </summary>
    private int? TryReadPageCount(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount;
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or IOException)
        {
            logger.LogDebug("Lecture PDF impossible ({Reason}) : {Path}", ex.Message, path);
            return null;
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
