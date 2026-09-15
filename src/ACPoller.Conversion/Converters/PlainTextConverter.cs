using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace ACPoller.Conversion.Converters;

/// <summary>
/// Convertit les fichiers texte en PDF, sans passer par LibreOffice.
/// </summary>
/// <remarks>
/// Priorite superieure a LibreOffice, qui sait pourtant traiter le .txt :
/// lancer un process de plusieurs centaines de mega-octets pour un fichier de
/// deux cents octets n'a pas de sens, et cela occupe un slot du pool
/// LibreOffice qu'un vrai document attend.
/// </remarks>
public sealed class PlainTextConverter(
    TextPdfRenderer renderer,
    ILogger<PlainTextConverter> logger) : IDocumentConverter
{
    // Au-dela, le fichier n'est pas un document a archiver mais un journal ou
    // un export de donnees : le rendu integral produirait des milliers de pages.
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    private static readonly string[] SupportedExtensions =
    [
        ".txt", ".log", ".csv", ".json", ".xml", ".md", ".ini", ".cfg",
    ];

    /// <inheritdoc />
    public string Name => "PlainText";

    /// <inheritdoc />
    public int Priority => 300;

    /// <inheritdoc />
    public bool CanHandle(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();

        if (!SupportedExtensions.Contains(extension))
        {
            return false;
        }

        // Un gros fichier repart vers LibreOffice, mieux arme pour la mise en
        // page d'un CSV volumineux.
        return new FileInfo(request.SourcePath).Length <= MaxSizeBytes;
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

        try
        {
            // Detection d'encodage : un fichier produit par un ERP est souvent
            // en Windows-1252, et le lire en UTF-8 remplace tous les accents
            // par des losanges.
            var text = await ReadTextAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);

            var pages = await renderer
                .RenderAsync(text, outputPath, request.FileName, cancellationToken)
                .ConfigureAwait(false);

            return ConversionResult.Success(outputPath, Name, pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            return ConversionResult.Failed(
                Failure.Transient($"Lecture impossible : {ex.Message}", exception: ex), Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rendu texte en echec : {LogicalPath}", request.LogicalPath);
            return ConversionResult.Failed(
                Failure.Permanent($"Rendu texte impossible : {ex.Message}", exception: ex), Name);
        }
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        // Marque d'ordre des octets presente : l'encodage est explicite.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        // UTF-8 strict : leve si les octets ne forment pas de l'UTF-8 valide,
        // ce qui permet de basculer sur Windows-1252 sans deviner.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
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

/// <summary>
/// Produit le PDF de substitution utilise par la politique
/// <c>ConversionFailurePolicy.Substitute</c>.
/// </summary>
/// <remarks>
/// Le principe : une piece jointe non convertible ne disparait pas du dossier.
/// La GED recoit une page portant le nom du fichier, sa taille, son type et le
/// motif de l'echec. L'utilisateur metier voit qu'il manque quelque chose et
/// sait quoi demander, au lieu de decouvrir un trou six mois plus tard.
/// </remarks>
public sealed class SubstitutePdfWriter(TextPdfRenderer renderer) : ISubstituteDocumentWriter
{
    /// <summary>Ecrit la page de substitution pour un noeud en echec.</summary>
    /// <param name="node">Noeud non converti.</param>
    /// <param name="outputDirectory">Repertoire de sortie.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le chemin du PDF produit.</returns>
    public async Task<string> WriteAsync(
        DocumentNode node,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        var outputPath = Path.Combine(
            outputDirectory,
            SanitizeSegment(node.LogicalPath) + ".substitut.pdf");

        var builder = new StringBuilder();
        builder.AppendLine("Cette piece jointe n'a pas pu etre convertie en PDF.");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Fichier      : {node.FileName}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Emplacement  : {node.LogicalPath}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Type declare : {node.ContentType ?? "inconnu"}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Taille       : {node.SizeBytes:N0} octets");

        if (node.Sha256 is not null)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Empreinte    : {node.Sha256}");
        }

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Motif : {node.Failure?.Reason ?? "inconnu"}");
        builder.AppendLine();
        builder.AppendLine("Le fichier d'origine reste disponible aupres de l'expediteur.");

        await renderer
            .RenderAsync(builder.ToString(), outputPath, "Piece jointe non convertie", cancellationToken)
            .ConfigureAwait(false);

        return outputPath;
    }

    private static string SanitizeSegment(string logicalPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. logicalPath.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' ? '_' : c)]);

        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
