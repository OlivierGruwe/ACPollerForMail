using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ACPoller.Conversion.Extraction;

/// <summary>
/// Developpe un message imbrique (.eml) en reutilisant le parseur MIME.
/// </summary>
/// <remarks>
/// L'extracteur delegue entierement au parseur plutot que de reimplementer la
/// lecture MIME. C'est ce qui garantit qu'un message transfere est traite
/// exactement comme un message recu : meme classification des pieces, meme
/// gestion du corps absent, mêmes noms de fichiers. Deux implementations
/// finiraient par diverger, et le bug ne se verrait que sur les transferts.
///
/// La recursion s'arrete par les bornes de <see cref="ExtractionLimits"/>,
/// appliquees par le DocumentTreeBuilder qui rappelle cet extracteur sur les
/// enfants produits.
/// </remarks>
public sealed class EmlExtractor(IMessageParser parser, ILogger<EmlExtractor> logger) : IContainerExtractor
{
    /// <inheritdoc />
    public string Name => "Eml";

    /// <inheritdoc />
    public bool CanHandle(string fileName, string? contentType)
    {
        if (Path.GetExtension(fileName).Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return contentType?.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <inheritdoc />
    public async Task ExtractAsync(
        DocumentNode node,
        string targetDirectory,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.SourcePath is null)
        {
            return;
        }

        MessageEnvelope envelope;

        try
        {
            await using var headerStream = File.OpenRead(node.SourcePath);
            var message = await MimeMessage.LoadAsync(headerStream, cancellationToken).ConfigureAwait(false);
            envelope = ToEnvelope(message);
        }
        catch (FormatException ex)
        {
            // Un .eml illisible degrade ce noeud sans interrompre le message
            // porteur : les autres pieces jointes restent traitees.
            logger.LogWarning(ex, "Message imbrique illisible : {LogicalPath}", node.LogicalPath);
            node.Properties["ExtractionWarning"] = "Message imbrique illisible.";
            return;
        }

        await using var stream = File.OpenRead(node.SourcePath);

        // Le prefixe garantit l'unicite des chemins logiques dans tout l'arbre :
        // sans lui, deux "corps.html" a deux niveaux differents porteraient la
        // meme cle de reprise et se telescoperaient.
        var nested = await parser
            .ParseAsync(stream, targetDirectory, envelope, node.LogicalPath + "/", cancellationToken)
            .ConfigureAwait(false);

        foreach (var child in nested.Children)
        {
            node.AddChild(child);
        }

        // Les en-tetes du message imbrique sont conserves : un traitement metier
        // peut avoir besoin de l'expediteur d'origine, pas de celui qui a transfere.
        if (envelope.Subject is not null)
        {
            node.Properties["NestedSubject"] = envelope.Subject;
        }

        if (envelope.From is not null)
        {
            node.Properties["NestedFrom"] = envelope.From;
        }

        logger.LogDebug(
            "Message imbrique {LogicalPath} : {Count} piece(s)",
            node.LogicalPath,
            nested.Children.Count);
    }

    private static MessageEnvelope ToEnvelope(MimeMessage message) => new()
    {
        Subject = message.Subject,
        From = message.From.Mailboxes.FirstOrDefault()?.Address,
        To = [.. message.To.Mailboxes.Select(m => m.Address)],
        Cc = [.. message.Cc.Mailboxes.Select(m => m.Address)],
        ReceivedUtc = message.Date.UtcDateTime,
        SentUtc = message.Date,
    };
}
