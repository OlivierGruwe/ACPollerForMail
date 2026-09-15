using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;

namespace ACPoller.Conversion.Extraction;

/// <summary>
/// Developpe une archive TAR, y compris compressee en gzip.
/// </summary>
/// <remarks>
/// Les memes garde-fous que pour le ZIP s'appliquent, avec une difference de
/// fond : un flux TAR n'a PAS de table centrale, on ne connait donc la taille
/// d'une entree qu'apres l'avoir lue. Le controle de volume se fait par
/// consequent APRES ecriture, et le fichier est supprime s'il depasse. C'est
/// moins elegant que pour le ZIP, mais c'est la seule facon de borner un format
/// qui ne s'annonce pas.
///
/// Consequence a assumer : un tar.gz malveillant peut ecrire jusqu'a une
/// entree au-dela de la limite avant d'etre arrete. La borne unitaire limite
/// donc le degat au maximum d'une seule entree.
/// </remarks>
public sealed class TarExtractor(ILogger<TarExtractor> logger) : IContainerExtractor
{
    /// <inheritdoc />
    public string Name => "Tar";

    /// <inheritdoc />
    public bool CanHandle(string fileName, string? contentType)
    {
        var lower = fileName.ToLowerInvariant();

        return lower.EndsWith(".tar", StringComparison.Ordinal)
            || lower.EndsWith(".tar.gz", StringComparison.Ordinal)
            || lower.EndsWith(".tgz", StringComparison.Ordinal)
            || contentType?.Contains("x-tar", StringComparison.OrdinalIgnoreCase) == true;
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

        var root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        var compressed = IsGzip(node.FileName);

        await using var fileStream = File.OpenRead(node.SourcePath);

        // Le flux gzip est enveloppe et non decompresse en memoire : une
        // archive de plusieurs centaines de mega-octets ne doit pas etre
        // materialisee pour etre lue.
        await using var source = compressed
            ? new GZipStream(fileStream, CompressionMode.Decompress)
            : (Stream)fileStream;

        await using var reader = new TarReader(source, leaveOpen: true);

        var extracted = 0;
        long totalBytes = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
            is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Repertoires, liens symboliques et durs : ignores. Un lien
            // symbolique pointant hors de l'arborescence est l'equivalent tar
            // du zip slip, et rien n'oblige a le suivre.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            if (entry.DataStream is null)
            {
                continue;
            }

            if (extracted >= limits.MaxNodes)
            {
                Refuse(node, $"Archive tronquee : plus de {limits.MaxNodes} entrees.");
                break;
            }

            var safeName = FileNameSanitizer.MakeUnique(
                FileNameSanitizer.Sanitize(Path.GetFileName(entry.Name), "entree.bin"),
                usedNames);
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, safeName));

            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Entree {Entry} refusee : chemin hors du repertoire cible", entry.Name);
                continue;
            }

            await using (var target = File.Create(destination))
            {
                await entry.DataStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(destination);

            // Controle a posteriori : la taille n'est connue qu'apres lecture.
            if (info.Length > limits.MaxSingleEntryBytes)
            {
                File.Delete(destination);
                logger.LogWarning(
                    "Entree {Entry} ecartee : {Size} octets depasse la limite unitaire",
                    entry.Name, info.Length);
                continue;
            }

            totalBytes += info.Length;

            if (totalBytes > limits.MaxExpandedBytes)
            {
                File.Delete(destination);
                Refuse(node, $"Volume decompresse depasse ({limits.MaxExpandedBytes} octets).");
                break;
            }

            extracted++;

            node.AddChild(new DocumentNode(
                $"{node.LogicalPath}/{safeName}", ClassifyKind(safeName), safeName)
            {
                SourcePath = destination,
                SizeBytes = info.Length,
                Sha256 = await ComputeHashAsync(destination, cancellationToken).ConfigureAwait(false),
            });
        }

        logger.LogDebug(
            "Archive {LogicalPath} : {Count} entree(s) extraite(s), {Bytes} octets",
            node.LogicalPath, extracted, totalBytes);
    }

    private static bool IsGzip(string fileName) =>
        fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private void Refuse(DocumentNode node, string reason)
    {
        logger.LogWarning("Extraction bornee pour {LogicalPath} : {Reason}", node.LogicalPath, reason);
        node.Properties["ExtractionWarning"] = reason;
    }

    private static DocumentKind ClassifyKind(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" => DocumentKind.Archive,
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
