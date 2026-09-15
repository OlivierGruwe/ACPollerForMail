using System.Security.Cryptography;
using System.Text;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ACPoller.Conversion.Parsing;

/// <summary>
/// Construit l'arbre documentaire a partir du MIME brut, via MimeKit.
/// </summary>
/// <remarks>
/// Une seule implementation pour IMAP et Graph, puisque les deux fournissent le
/// meme MIME. C'est ce qui evite d'avoir deux modeles objets divergents et deux
/// jeux de bugs distincts selon le protocole.
///
/// Le parseur sert aussi a developper les messages imbriques, appele par
/// l'extracteur .eml avec le prefixe du noeud porteur.
/// </remarks>
public sealed class MimeMessageParser(ILogger<MimeMessageParser> logger) : IMessageParser
{
    /// <inheritdoc />
    public async Task<DocumentNode> ParseAsync(
        Stream rawMessage,
        string workDirectory,
        MessageEnvelope envelope,
        string logicalPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        ArgumentNullException.ThrowIfNull(logicalPrefix);

        var message = await MimeMessage.LoadAsync(rawMessage, cancellationToken).ConfigureAwait(false);

        // Pour le message racine, les pieces vont dans un sous-repertoire dedie.
        // Pour un message imbrique, le repertoire est deja celui que l'extracteur
        // a prepare pour ce noeud : y ajouter un niveau allongerait les chemins
        // pour rien, et Windows a une limite.
        var partsDirectory = logicalPrefix.Length == 0
            ? Path.Combine(workDirectory, "parts")
            : workDirectory;

        Directory.CreateDirectory(partsDirectory);

        var root = new DocumentNode(logicalPrefix, DocumentKind.Attachment, "message")
        {
            Status = NodeStatus.Container,
            ContentType = "message/rfc822",
        };

        await AddBodyAsync(message, root, partsDirectory, logicalPrefix, cancellationToken).ConfigureAwait(false);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in message.BodyParts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await AddPartAsync(part, root, partsDirectory, logicalPrefix, usedNames, cancellationToken)
                .ConfigureAwait(false);
        }

        return root;
    }

    private async Task AddBodyAsync(
        MimeMessage message,
        DocumentNode root,
        string directory,
        string logicalPrefix,
        CancellationToken cancellationToken)
    {
        // Un message peut n'avoir AUCUNE partie texte : que des pieces jointes,
        // ou un MIME exotique. C'est le cas qui produisait le NullReference en
        // v1, parce que le corps n'etait cree que dans la branche texte.
        // Ici, l'absence de corps signifie simplement l'absence de noeud.
        var html = message.HtmlBody;
        var text = message.TextBody;

        if (html is null && text is null)
        {
            logger.LogDebug("Message sans partie texte ni HTML : aucun noeud de corps cree");
            return;
        }

        var isHtml = html is not null;
        var content = html ?? text!;
        var fileName = isHtml ? "corps.html" : "corps.txt";
        var path = Path.Combine(directory, fileName);

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var node = new DocumentNode(logicalPrefix + fileName, DocumentKind.Body, fileName)
        {
            ContentType = isHtml ? "text/html" : "text/plain",
            SourcePath = path,
            SizeBytes = new FileInfo(path).Length,
        };

        node.Sha256 = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);

        // Le corps est toujours le premier noeud : il ouvre le PDF unifie.
        root.AddChild(node);
    }

    private async Task AddPartAsync(
        MimeEntity entity,
        DocumentNode parent,
        string directory,
        string logicalPrefix,
        HashSet<string> usedNames,
        CancellationToken cancellationToken)
    {
        switch (entity)
        {
            case MessagePart messagePart:
                {
                    if (messagePart.Message is null)
                    {
                        logger.LogWarning("Message imbrique vide ignore");
                        return;
                    }

                    // Message imbrique : ecrit tel quel en .eml. Son developpement
                    // est le role de l'extracteur, pas du parseur, pour que les
                    // garde-fous de recursion s'appliquent uniformement.
                    var name = FileNameSanitizer.MakeUnique(
                         FileNameSanitizer.Sanitize(messagePart.Message.Subject ?? "message") + ".eml",
                         usedNames);
                    var path = Path.Combine(directory, name);

                    await using (var stream = File.Create(path))
                    {
                        await messagePart.Message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
                    }

                    var node = new DocumentNode(logicalPrefix + name, DocumentKind.EmbeddedMessage, name)
                    {
                        ContentType = "message/rfc822",
                        SourcePath = path,
                        SizeBytes = new FileInfo(path).Length,
                    };

                    node.Sha256 = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
                    parent.AddChild(node);
                    return;
                }

            case MimePart part:
                {
                    // Les parties texte deja restituees comme corps ne sont pas
                    // rejouees en pieces jointes : sinon chaque mail produit un
                    // doublon du corps dans la sortie.
                    if (part is TextPart && !part.IsAttachment && part.ContentId is null)
                    {
                        return;
                    }

                    // Une MimePart sans contenu existe : partie vide ou MIME mal
                    // forme. Sans ce controle, un mail atypique fait tomber le
                    // parsing entier alors qu'une piece vide est ignorable.
                    if (part.Content is null)
                    {
                        logger.LogWarning(
                            "Piece jointe sans contenu ignoree : {Name}",
                            part.FileName ?? "sans nom");
                        return;
                    }

                    var name = FileNameSanitizer.MakeUnique(ResolveFileName(part), usedNames);
                    var path = Path.Combine(directory, name);

                    await using (var stream = File.Create(path))
                    {
                        await part.Content.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
                    }

                    var kind = ClassifyKind(part);

                    var node = new DocumentNode(logicalPrefix + name, kind, name)
                    {
                        ContentType = part.ContentType?.MimeType,
                        SourcePath = path,
                        SizeBytes = new FileInfo(path).Length,
                    };

                    node.Sha256 = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);

                    if (part.ContentId is not null)
                    {
                        node.Properties["ContentId"] = part.ContentId.Trim('<', '>');
                    }

                    parent.AddChild(node);
                    return;
                }

            default:
                // Multipart et types composites : MimeKit les a deja aplatis
                // dans BodyParts, rien a faire ici.
                return;
        }
    }

    private static DocumentKind ClassifyKind(MimePart part)
    {
        var isImage = part.ContentType?.MediaType?.Equals("image", StringComparison.OrdinalIgnoreCase) == true;

        // Image referencee par le corps HTML : c'est presque toujours un logo de
        // signature. Le pipeline la filtre ensuite sur sa taille.
        if (isImage && part.ContentId is not null && !part.IsAttachment)
        {
            return DocumentKind.InlineImage;
        }

        var name = part.FileName ?? string.Empty;

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => DocumentKind.Archive,
            ".eml" or ".msg" => DocumentKind.EmbeddedMessage,
            _ => DocumentKind.Attachment,
        };
    }

    private static string ResolveFileName(MimePart part)
    {
        var name = part.FileName;

        if (!string.IsNullOrWhiteSpace(name))
        {
            return FileNameSanitizer.Sanitize(name);
        }

        // Piece jointe sans nom : frequent sur les images incorporees et les
        // signatures. Un nom deduit du type MIME vaut mieux qu'un fichier
        // sans extension que plus aucun convertisseur ne saura reconnaitre.
        var extension = part.ContentType?.MediaSubtype?.ToLowerInvariant() switch
        {
            "pdf" => ".pdf",
            "jpeg" => ".jpg",
            "png" => ".png",
            "gif" => ".gif",
            "tiff" => ".tif",
            "plain" => ".txt",
            "html" => ".html",
            _ => ".bin",
        };

        return "piece" + extension;
    }

    //private static string SanitizeFileName(string name)
    //{
    //    var invalid = Path.GetInvalidFileNameChars();
    //    var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();

    //    // Un nom trop long fait echouer la creation du fichier sur Windows,
    //    // d'autant que le repertoire de travail est deja profond.
    //    if (cleaned.Length > 100)
    //    {
    //        var extension = Path.GetExtension(cleaned);
    //        var stem = Path.GetFileNameWithoutExtension(cleaned);
    //        cleaned = string.Concat(stem.AsSpan(0, Math.Min(90, stem.Length)), extension);
    //    }

    //    return cleaned.Length == 0 ? "piece.bin" : cleaned;
    //}

    ///// <summary>
    ///// Deux pieces jointes peuvent porter le meme nom dans un meme message :
    ///// sans desambiguisation, la seconde ecrase la premiere en silence.
    ///// </summary>
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
