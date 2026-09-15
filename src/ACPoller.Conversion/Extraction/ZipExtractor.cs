using System.IO.Compression;
using System.Security.Cryptography;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;

namespace ACPoller.Conversion.Extraction;

/// <summary>
/// Developpe une archive ZIP en noeuds enfants.
/// </summary>
/// <remarks>
/// Les archives traitees ici viennent de mails, donc de n'importe qui. Trois
/// attaques sont couvertes explicitement :
///
/// 1. Zip slip : une entree nommee "..\..\windows\system32\x.dll" ecrirait hors
///    du repertoire de travail. Le chemin resolu est verifie, pas le chemin declare.
/// 2. Bombe de decompression : quelques kilo-octets qui en produisent des
///    giga-octets. Taille par entree, volume cumule et taux de compression sont
///    tous bornes.
/// 3. Saturation par le nombre : des millions d'entrees minuscules. Le nombre
///    d'entrees est borne.
///
/// Aucune de ces bornes n'est desactivable par configuration : seules leurs
/// valeurs le sont.
/// </remarks>
public sealed class ZipExtractor(ILogger<ZipExtractor> logger) : IContainerExtractor
{
    // Au-dela de ce taux, une entree est refusee. Un document bureautique
    // depasse rarement 20:1, un fichier de zeros atteint 1000:1.
    private const int MaxCompressionRatio = 200;

    /// <inheritdoc />
    public string Name => "Zip";

    /// <inheritdoc />
    public bool CanHandle(string fileName, string? contentType)
    {
        if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return contentType is not null
            && (contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/x-compressed", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task ExtractAsync(
        DocumentNode node,
        string targetDirectory,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(limits);

        if (node.SourcePath is null)
        {
            return;
        }

        Directory.CreateDirectory(targetDirectory);

        // Chemin canonique du repertoire cible, base de la verification anti-slip.
        var root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        using var archive = await ZipFile.OpenReadAsync(node.SourcePath, cancellationToken).ConfigureAwait(false);

        var extracted = 0;
        long totalBytes = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Entree de repertoire : pas de contenu, rien a extraire.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (extracted >= limits.MaxNodes)
            {
                Refuse(node, $"Archive tronquee : plus de {limits.MaxNodes} entrees.");
                break;
            }

            if (entry.Length > limits.MaxSingleEntryBytes)
            {
                logger.LogWarning(
                    "Entree {Entry} ignoree : {Size} octets depasse la limite unitaire",
                    entry.FullName, entry.Length);
                continue;
            }

            if (totalBytes + entry.Length > limits.MaxExpandedBytes)
            {
                Refuse(node, $"Volume decompresse depasse ({limits.MaxExpandedBytes} octets).");
                break;
            }

            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
            {
                logger.LogWarning(
                    "Entree {Entry} refusee : taux de compression {Ratio}:1 anormal",
                    entry.FullName, entry.Length / entry.CompressedLength);
                continue;
            }

            // Le nom est aplati : la hierarchie interne de l'archive n'a pas de
            // sens dans une GED, et un chemin conserve rouvre la porte au zip slip.
            var safeName = FileNameSanitizer.MakeUnique(
                FileNameSanitizer.Sanitize(Path.GetFileName(entry.Name), "entree.bin"),
                usedNames);
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, safeName));

            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Entree {Entry} refusee : chemin hors du repertoire cible", entry.FullName);
                continue;
            }

            await using (var source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(destination))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(destination);
            totalBytes += info.Length;
            extracted++;

            var child = new DocumentNode(
                $"{node.LogicalPath}/{safeName}",
                ClassifyKind(safeName),
                safeName)
            {
                SourcePath = destination,
                SizeBytes = info.Length,
                Sha256 = await ComputeHashAsync(destination, cancellationToken).ConfigureAwait(false),
            };

            node.AddChild(child);
        }

        logger.LogDebug(
            "Archive {LogicalPath} : {Count} entree(s) extraite(s), {Bytes} octets",
            node.LogicalPath, extracted, totalBytes);
    }

    private void Refuse(DocumentNode node, string reason)
    {
        logger.LogWarning("Extraction bornee pour {LogicalPath} : {Reason}", node.LogicalPath, reason);
        node.Properties["ExtractionWarning"] = reason;
    }

    private static DocumentKind ClassifyKind(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".rar" => DocumentKind.Archive,
            ".eml" or ".msg" => DocumentKind.EmbeddedMessage,
            _ => DocumentKind.Attachment,
        };

    //private static string SanitizeFileName(string name)
    //{
    //    var invalid = Path.GetInvalidFileNameChars();
    //    var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();

    //    if (cleaned.Length > 100)
    //    {
    //        var extension = Path.GetExtension(cleaned);
    //        var stem = Path.GetFileNameWithoutExtension(cleaned);
    //        cleaned = string.Concat(stem.AsSpan(0, Math.Min(90, stem.Length)), extension);
    //    }

    //    return cleaned.Length == 0 ? "entree.bin" : cleaned;
    //}

    //private static string UniqueName(string name, HashSet<string> used)
    //{
    //    if (used.Add(name))
    //    {
    //        return name;
    //    }

    //    var stem = Path.GetFileNameWithoutExtension(name);
    //    var extension = Path.GetExtension(name);

    //    for (var i = 2; ; i++)
    //    {
    //        var candidate = $"{stem}_{i}{extension}";
    //        if (used.Add(candidate))
    //        {
    //            return candidate;
    //        }
    //    }
    //}

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
