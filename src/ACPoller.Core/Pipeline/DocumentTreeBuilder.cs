using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Pipeline;

/// <summary>
/// Parse un MIME brut en arbre documentaire. Une seule implementation, MimeKit,
/// partagee par IMAP et Graph puisque les deux fournissent le meme MIME.
/// </summary>
public interface IMessageParser
{
    /// <summary>
    /// Construit la racine et ses enfants directs (corps, pieces jointes) et
    /// extrait chaque piece dans <paramref name="workDirectory"/>.
    /// </summary>
    /// <param name="rawMessage">Flux MIME brut.</param>
    /// <param name="workDirectory">Repertoire ou extraire les pieces.</param>
    /// <param name="envelope">En-tetes du message.</param>
    /// <param name="logicalPrefix">
    /// Prefixe des chemins logiques, vide pour le message racine. Un message
    /// imbrique passe le chemin de son propre noeud, pour que la cle de reprise
    /// reste unique dans tout l'arbre.
    /// </param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>La racine de l'arbre construit.</returns>
    Task<DocumentNode> ParseAsync(
        Stream rawMessage,
        string workDirectory,
        MessageEnvelope envelope,
        string logicalPrefix,
        CancellationToken cancellationToken);
}

/// <summary>
/// Produit un PDF de substitution pour une piece non convertible.
/// </summary>
/// <remarks>
/// Declaree ici et non dans le projet de conversion : le pipeline en a besoin,
/// et le Core ne doit pas dependre d'une implementation concrete.
/// </remarks>
public interface ISubstituteDocumentWriter
{
    /// <summary>Ecrit la page de substitution pour un noeud en echec.</summary>
    /// <param name="node">Noeud non converti.</param>
    /// <param name="outputDirectory">Repertoire de sortie.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le chemin du PDF produit.</returns>
    Task<string> WriteAsync(DocumentNode node, string outputDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Developpe recursivement les conteneurs (archives, messages imbriques) sous
/// les bornes de <see cref="ExtractionLimits"/>.
/// </summary>
/// <remarks>
/// Les bornes ne sont pas des precautions theoriques : le service ouvre des
/// fichiers arrivant par mail, donc fournis par n'importe qui. Une archive
/// recursive de quelques kilo-octets suffit a saturer un disque, et un eml
/// s'auto-referencant a boucler indefiniment.
/// </remarks>
public sealed class DocumentTreeBuilder(
    IEnumerable<IContainerExtractor> extractors,
    ILogger<DocumentTreeBuilder> logger)
{
    private readonly IContainerExtractor[] _extractors = [.. extractors];

    public async Task ExpandAsync(
        CaptureContext context,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var budget = new ExtractionBudget(limits);

        // Parcours en largeur : la profondeur croit lentement, donc un depassement
        // de borne est detecte avant d'avoir extrait des milliers de fichiers.
        var queue = new Queue<DocumentNode>(context.Root.Children);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = queue.Dequeue();

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(node.FileName, node.ContentType));
            if (extractor is null || node.SourcePath is null)
            {
                continue;
            }

            if (node.Depth >= limits.MaxDepth)
            {
                Refuse(context, node, $"Profondeur maximale atteinte ({limits.MaxDepth}).");
                continue;
            }

            if (!budget.TryConsumeNode())
            {
                Refuse(context, node, $"Nombre maximal de noeuds atteint ({limits.MaxNodes}).");
                continue;
            }

            if (!budget.TryConsumeBytes(node.SizeBytes))
            {
                Refuse(context, node, $"Volume decompresse maximal atteint ({limits.MaxExpandedBytes} octets).");
                continue;
            }

            var targetDirectory = Path.Combine(
                context.WorkDirectory,
                "expanded",
                SanitizeSegment(node.LogicalPath));

            Directory.CreateDirectory(targetDirectory);

            try
            {
                await extractor.ExtractAsync(node, targetDirectory, limits, cancellationToken)
                    .ConfigureAwait(false);

                node.Status = NodeStatus.Container;

                foreach (var child in node.Children)
                {
                    queue.Enqueue(child);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Un conteneur illisible degrade ce noeud, il n'interrompt pas
                // le traitement des autres pieces du message.
                logger.LogWarning(ex, "Extraction impossible pour {LogicalPath}", node.LogicalPath);
                node.Status = NodeStatus.Failed;
                node.Failure = Failure.Permanent($"Extraction impossible : {ex.Message}", exception: ex);
                context.Warnings.Add(node.Failure);
            }
        }
    }

    private void Refuse(CaptureContext context, DocumentNode node, string reason)
    {
        logger.LogWarning("Extraction refusee pour {LogicalPath} : {Reason}", node.LogicalPath, reason);
        node.Status = NodeStatus.Excluded;
        node.Failure = Failure.Permanent(reason);
        context.Warnings.Add(node.Failure);
    }

    private static string SanitizeSegment(string logicalPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = logicalPath.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var cleaned = new string(chars);

        // Un nom trop long fait echouer la creation du repertoire sur Windows.
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private sealed class ExtractionBudget(ExtractionLimits limits)
    {
        private int _nodes;
        private long _bytes;

        public bool TryConsumeNode() => ++_nodes <= limits.MaxNodes;

        public bool TryConsumeBytes(long bytes)
        {
            _bytes += bytes;
            return _bytes <= limits.MaxExpandedBytes;
        }
    }
}
