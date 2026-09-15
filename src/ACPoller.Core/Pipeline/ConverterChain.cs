using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Infrastructure;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Pipeline;

/// <summary>
/// Applique la chaine de convertisseurs a chaque noeud de l'arbre.
/// </summary>
/// <remarks>
/// Reprise : un noeud deja converti lors d'une tentative precedente est saute.
/// C'est ce qui evite de repayer une conversion, ou pire un appel OCR facture,
/// parce qu'une cible d'export etait indisponible.
/// </remarks>
public sealed class ConverterChain(
    IEnumerable<IDocumentConverter> converters,
    ISubstituteDocumentWriter substituteWriter,
    IMetricsProvider metrics,
    ILogger<ConverterChain> logger)
{
    private readonly IDocumentConverter[] _converters =
        [.. converters.OrderByDescending(c => c.Priority)];

    /// <summary>Convertit en PDF tous les noeuds convertibles de l'arbre.</summary>
    /// <param name="context">Contexte du message.</param>
    /// <param name="options">Reglages de conversion de la configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee quand tous les noeuds ont ete traites.</returns>
    public async Task ConvertAsync(
        CaptureContext context,
        ConversionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var outputDirectory = Path.Combine(context.WorkDirectory, "pdf");
        Directory.CreateDirectory(outputDirectory);

        // Materialise : ApplyFailure peut ajouter un PDF de substitution, donc
        // modifier l'etat des noeuds pendant que l'arbre est parcouru.
        var nodes = context.Root.Walk().ToArray();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldConvert(node, options))
            {
                continue;
            }

            await ConvertNodeAsync(context, node, options, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool ShouldConvert(DocumentNode node, ConversionOptions options)
    {
        // Reprise apres incident : le travail deja fait n'est pas refait,
        // substitution comprise.
        if (node.Status is NodeStatus.Converted or NodeStatus.PassThrough or NodeStatus.Substituted
            && node.PdfPath is not null)
        {
            return false;
        }

        if (node.Status is NodeStatus.Container or NodeStatus.Excluded)
        {
            return false;
        }

        if (node.SourcePath is null)
        {
            return false;
        }

        if (node.Kind == DocumentKind.Body && !options.IncludeBody)
        {
            return false;
        }

        if (node.Kind == DocumentKind.InlineImage && node.SizeBytes < options.MinInlineImageBytes)
        {
            // Statut explicite : Pending se lit comme "pas encore traite" et
            // fait chercher une panne la ou il y a un filtre.
            node.Status = NodeStatus.Excluded;
            node.Failure = Failure.Unsupported("Image incorporee sous le seuil de taille.");
            return false;
        }

        var extension = Path.GetExtension(node.FileName);
        return !options.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private async Task ConvertNodeAsync(
        CaptureContext context,
        DocumentNode node,
        ConversionOptions options,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var request = new ConversionRequest
        {
            SourcePath = node.SourcePath!,
            FileName = node.FileName,
            ContentType = node.ContentType,
            OutputDirectory = outputDirectory,
            LogicalPath = node.LogicalPath,
            Timeout = options.Timeout,
        };

        var converter = _converters.FirstOrDefault(c => c.CanHandle(request));

        if (converter is null)
        {
            await ApplyFailureAsync(
                context, node, Failure.Unsupported($"Aucun convertisseur pour '{node.FileName}'."),
                options, outputDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            // Le timeout est porte par le jeton : un convertisseur hors process
            // doit etre tue, pas attendu.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);

            var result = await converter.ConvertAsync(request, timeout.Token).ConfigureAwait(false);

            if (result.Status is NodeStatus.Converted or NodeStatus.PassThrough)
            {
                node.Status = result.Status;
                node.PdfPath = result.PdfPath;
                node.PageCount = result.PageCount;
                node.ConvertedBy = result.ConverterName;
                return;
            }

            await ApplyFailureAsync(
                context, node, result.Failure ?? Failure.Permanent("Conversion echouee."),
                options, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ApplyFailureAsync(
                context, node, Failure.Transient($"Conversion interrompue apres {options.Timeout}."),
                options, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversion en erreur pour {LogicalPath}", node.LogicalPath);

            await ApplyFailureAsync(
                context, node, Failure.Permanent(ex.Message, exception: ex),
                options, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyFailureAsync(
        CaptureContext context,
        DocumentNode node,
        Failure failure,
        ConversionOptions options,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        node.Status = NodeStatus.Failed;
        node.Failure = failure;
        context.Warnings.Add(failure);

        // Ventile par configuration ET par nature d'echec : un flux qui
        // accumule des Unsupported signale un format non pris en charge, un
        // flux qui accumule des Transient signale un convertisseur en
        // difficulte. Les confondre masquerait la difference.
        metrics.Record(MetricKind.Counter, "conversion.failed", 1, new Dictionary<string, string>
        {
            ["configuration"] = context.ConfigurationName,
            ["kind"] = failure.Kind.ToString(),
        });

        logger.LogWarning(
            "Noeud {LogicalPath} non converti ({Kind}) : {Reason}",
            node.LogicalPath,
            failure.Kind,
            failure.Reason);


        switch (options.OnFailure)
        {
            case ConversionFailurePolicy.RejectMessage:
                // Aucune sortie partielle : le message entier est rejete.
                throw new AcPollerException(failure);

            case ConversionFailurePolicy.Substitute:
                await WriteSubstituteAsync(node, outputDirectory, cancellationToken).ConfigureAwait(false);
                break;

            case ConversionFailurePolicy.Skip:
            default:
                // Le noeud reste en echec, sans PDF. Le fichier d'information
                // en portera la trace, la GED ne verra rien de cette piece.
                break;
        }
    }

    private async Task WriteSubstituteAsync(
        DocumentNode node,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await substituteWriter
                .WriteAsync(node, outputDirectory, cancellationToken)
                .ConfigureAwait(false);

            // Statut distinct de Converted : le PDF part bien en sortie et
            // sera fusionne, mais rien ne doit laisser croire que la piece a
            // ete convertie. Le motif d'origine reste dans node.Failure et
            // ressort dans le fichier d'information.
            node.Status = NodeStatus.Substituted;
            node.PdfPath = path;
            node.PageCount = 1;
            node.ConvertedBy = "Substitute";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un echec de substitution ne doit pas masquer l'echec d'origine,
            // qui reste la vraie information.
            logger.LogWarning(ex, "PDF de substitution non produit pour {LogicalPath}", node.LogicalPath);
        }
    }
}
