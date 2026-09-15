using System.Security.Cryptography;
using System.Text;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;
using MsgReader.Outlook;

namespace ACPoller.Conversion.Extraction;

/// <summary>
/// Developpe un message Outlook (.msg) en noeuds enfants.
/// </summary>
/// <remarks>
/// Le format .msg est un fichier composite OLE, pas du MIME : MimeKit ne sait
/// pas le lire, d'ou une implementation distincte de l'extracteur .eml.
/// C'est pourtant un cas tres frequent en entreprise, puisque c'est ce que
/// produit un glisser-deposer depuis Outlook.
///
/// Point de vigilance : la bibliotheque sous-jacente ouvre des fichiers CFB
/// non fiables, et cette famille de formats a un historique de boucles
/// infinies sur entrees forgees. C'est une raison de plus pour que la
/// conversion tourne hors process, avec un timeout qui tue au lieu d'attendre.
/// </remarks>
public sealed class MsgExtractor(ILogger<MsgExtractor> logger) : IContainerExtractor
{
    /// <inheritdoc />
    public string Name => "Msg";

    /// <inheritdoc />
    public bool CanHandle(string fileName, string? contentType)
    {
        if (Path.GetExtension(fileName).Equals(".msg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return contentType?.Contains("vnd.ms-outlook", StringComparison.OrdinalIgnoreCase) == true;
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

        try
        {
            await using var stream = File.OpenRead(node.SourcePath);
            using var message = new Storage.Message(stream);

            await AddBodyAsync(node, message, targetDirectory, cancellationToken).ConfigureAwait(false);
            await AddAttachmentsAsync(node, message, targetDirectory, limits, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(message.Subject))
            {
                node.Properties["NestedSubject"] = message.Subject;
            }

            if (message.Sender?.Email is { Length: > 0 } sender)
            {
                node.Properties["NestedFrom"] = sender;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un .msg illisible degrade ce noeud sans interrompre le message
            // porteur. Le catch est large a dessein : la bibliotheque leve des
            // exceptions non documentees sur fichier malforme.
            logger.LogWarning(ex, "Message Outlook illisible : {LogicalPath}", node.LogicalPath);
            node.Properties["ExtractionWarning"] = "Message Outlook illisible.";
        }
    }

    private static async Task AddBodyAsync(
        DocumentNode node,
        Storage.Message message,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var html = message.BodyHtml;
        var text = message.BodyText;

        // Meme regle que pour le MIME : pas de corps, pas de noeud.
        if (string.IsNullOrEmpty(html) && string.IsNullOrEmpty(text))
        {
            return;
        }

        var isHtml = !string.IsNullOrEmpty(html);
        var fileName = isHtml ? "corps.html" : "corps.txt";
        var path = Path.Combine(targetDirectory, fileName);

        await File.WriteAllTextAsync(path, isHtml ? html! : text!, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        node.AddChild(new DocumentNode($"{node.LogicalPath}/{fileName}", DocumentKind.Body, fileName)
        {
            ContentType = isHtml ? "text/html" : "text/plain",
            SourcePath = path,
            SizeBytes = new FileInfo(path).Length,
        });
    }

    private async Task AddAttachmentsAsync(
        DocumentNode node,
        Storage.Message message,
        string targetDirectory,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "corps.html", "corps.txt" };
        var count = 0;

        foreach (var item in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (count >= limits.MaxNodes)
            {
                node.Properties["ExtractionWarning"] = $"Plus de {limits.MaxNodes} pieces jointes.";
                break;
            }

            switch (item)
            {
                case Storage.Attachment attachment:
                {
                    if (attachment.Data is null || attachment.Data.LongLength > limits.MaxSingleEntryBytes)
                    {
                        continue;
                    }

                        var name = FileNameSanitizer.MakeUnique(
                            FileNameSanitizer.Sanitize(attachment.FileName), used);
                        var path = Path.Combine(targetDirectory, name);

                    await File.WriteAllBytesAsync(path, attachment.Data, cancellationToken).ConfigureAwait(false);

                    node.AddChild(new DocumentNode(
                        $"{node.LogicalPath}/{name}", ClassifyKind(name), name)
                    {
                        SourcePath = path,
                        SizeBytes = attachment.Data.LongLength,
                        Sha256 = Convert.ToHexStringLower(SHA256.HashData(attachment.Data)),
                    });

                    count++;
                    break;
                }

                case Storage.Message nested:
                {
                        // Message imbrique dans un .msg : reecrit en .msg sur disque,
                        // son developpement revient au tour suivant du builder, sous
                        // les memes bornes de profondeur.
                        var name = FileNameSanitizer.MakeUnique(
                            FileNameSanitizer.Sanitize(nested.Subject ?? "message", "message") + ".msg", used);
                        var path = Path.Combine(targetDirectory, name);

                    nested.Save(path);

                    var info = new FileInfo(path);

                    node.AddChild(new DocumentNode(
                        $"{node.LogicalPath}/{name}", DocumentKind.EmbeddedMessage, name)
                    {
                        ContentType = "application/vnd.ms-outlook",
                        SourcePath = path,
                        SizeBytes = info.Length,
                    });

                    count++;
                    break;
                }

                default:
                    logger.LogDebug("Piece jointe de type inattendu ignoree : {Type}", item?.GetType().Name);
                    break;
            }
        }
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

    //    return cleaned.Length == 0 ? "piece.bin" : cleaned;
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
}
