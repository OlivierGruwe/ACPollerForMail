using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Infrastructure;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using ACPoller.Abstractions.Processing;
using ACPoller.Abstractions.Sources;
using ACPoller.Core.Configuration;
using ACPoller.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ACPoller.Core.Pipeline;

/// <summary>Issue du traitement d'un message.</summary>
public enum CaptureStatus
{
    /// <summary>Exporte avec succes sur toutes les cibles.</summary>
    Exported = 0,

    /// <summary>Ecarte par un traitement metier, sans erreur.</summary>
    Skipped = 1,

    /// <summary>Rejete par un traitement metier, avec un code.</summary>
    Rejected = 2,

    /// <summary>En echec. Le contexte est conserve pour reprise.</summary>
    Failed = 3,
}

/// <summary>Resultat du traitement d'un message.</summary>
/// <param name="Status">Issue du traitement.</param>
/// <param name="CorrelationId">Identifiant de correlation.</param>
/// <param name="RejectionCode">Code de rejet metier, en cas de rejet.</param>
/// <param name="Failure">Echec qualifie, en cas d'erreur.</param>
public sealed record CaptureResult(
    CaptureStatus Status,
    string CorrelationId,
    string? RejectionCode = null,
    Failure? Failure = null);

/// <summary>
/// Traite un message de bout en bout : telechargement, arbre documentaire,
/// conversion, assemblage, fichier d'information, export.
/// </summary>
/// <remarks>
/// Deux invariants d'ordre, tous deux issus d'incidents v1 :
///
/// 1. Le message n'est marque comme traite qu'APRES export confirme. En cas de
///    coupure entre les deux, il repart au cycle suivant. Un doublon se detecte
///    et se corrige, une perte silencieuse non.
/// 2. Le repertoire de travail n'est nettoye qu'apres export confirme, et il est
///    conserve en cas d'echec pour permettre la reprise et le diagnostic.
/// </remarks>
public sealed class CapturePipeline(
    IOptions<PollerOptions> pollerOptions,
    IMessageParser parser,
    DocumentTreeBuilder treeBuilder,
    ConverterChain converterChain,
    IPdfAssembler pdfAssembler,
    ExportCoordinator exportCoordinator,
    INameTemplateResolver nameResolver,
    ICaptureStateStore stateStore,
    IProcessedStore processedStore,
    IMetricsProvider metrics,
    MetadataFieldResolver fieldResolver,
    ILogger<CapturePipeline> logger)
{
    /// <summary>Traite un message.</summary>
    /// <param name="configuration">Configuration resolue de la boite.</param>
    /// <param name="source">Source de messages, deja connectee.</param>
    /// <param name="message">Message a traiter.</param>
    /// <param name="components">Composants resolus pour cette configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue du traitement.</returns>
    public async Task<CaptureResult> ProcessAsync(
        PollerConfiguration configuration,
        IMailSource source,
        MessageRef message,
        PipelineComponents components,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(components);

        var context = await PrepareContextAsync(configuration, source, message, cancellationToken)
            .ConfigureAwait(false);

        var started = DateTimeOffset.UtcNow;

        try
        {
            var outcome = await RunStagesAsync(configuration, context, components, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not null)
            {
                // Ecarte ou rejete : le message est considere comme traite, il
                // ne doit pas revenir au cycle suivant.
                await MarkAndDisposeAsync(configuration, source, message, context, cancellationToken)
                    .ConfigureAwait(false);

                return outcome;
            }

            var items = BuildExportItems(configuration, context);

            var exportResult = await exportCoordinator.ExportAsync(
                context,
                items,
                components.Targets,
                configuration.Output.Policy.AllOrNothing,
                cancellationToken).ConfigureAwait(false);

            if (!exportResult.Success)
            {
                // Le contexte reste persiste : la reprise ne reconvertira pas.
                await stateStore.SaveAsync(context, cancellationToken).ConfigureAwait(false);

                return new CaptureResult(
                    CaptureStatus.Failed,
                    context.CorrelationId,
                    Failure: exportResult.Failures.Count > 0 ? exportResult.Failures[0] : null);
            }

            await RunStageAsync(context, components, ProcessingStage.AfterExport, cancellationToken)
                .ConfigureAwait(false);

            // Ordre non negociable : export confirme, PUIS marquage, PUIS
            // disposition sur la boite, PUIS nettoyage.
            await MarkAndDisposeAsync(configuration, source, message, context, cancellationToken)
                .ConfigureAwait(false);

            metrics.Record(
                MetricKind.Duration,
                "capture.duration_ms",
                (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                new Dictionary<string, string> { ["configuration"] = configuration.Name });

            return new CaptureResult(CaptureStatus.Exported, context.CorrelationId);
        }
        catch (OperationCanceledException)
        {
            // Arret du service : le contexte est conserve tel quel pour reprise.
            await stateStore.SaveAsync(context, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (AcPollerException ex)
        {
            logger.LogError(ex, "Traitement en echec pour {CorrelationId}", context.CorrelationId);
            await stateStore.SaveAsync(context, cancellationToken).ConfigureAwait(false);
            return new CaptureResult(CaptureStatus.Failed, context.CorrelationId, Failure: ex.Failure);
        }
    }

    private async Task MarkAndDisposeAsync(
        PollerConfiguration configuration,
        IMailSource source,
        MessageRef message,
        CaptureContext context,
        CancellationToken cancellationToken)
    {
        await processedStore.MarkProcessedAsync(
            configuration.Source.Mailbox,
            context.Identity.DeduplicationKey,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        await source.ApplyDispositionAsync(
            message,
            configuration.Source.Disposition,
            ToFolderRef(configuration.Source.TargetFolder),
            cancellationToken).ConfigureAwait(false);

        await stateStore.RemoveAsync(context.CorrelationId, cancellationToken).ConfigureAwait(false);
        CleanWorkDirectory(context);
    }

    private async Task<CaptureContext> PrepareContextAsync(
        PollerConfiguration configuration,
        IMailSource source,
        MessageRef message,
        CancellationToken cancellationToken)
    {
        var correlationId = BuildCorrelationId(configuration, message);

        // Reprise : si une tentative precedente a laisse un contexte, on repart
        // de son arbre plutot que de retelecharger et tout reconvertir.
        var existing = await stateStore.LoadAsync(correlationId, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Attempt++;
            logger.LogInformation(
                "Reprise de {CorrelationId}, tentative {Attempt}",
                correlationId,
                existing.Attempt);

            return existing;
        }

        // Le repertoire de travail vient de la configuration, PAS du temporaire
        // systeme : le state store cherche les unites inachevees sous cette
        // racine, et un repertoire temporaire purge par Windows ferait perdre
        // toute reprise en cours.
        var workDirectory = Path.Combine(pollerOptions.Value.WorkDirectory, correlationId);
        Directory.CreateDirectory(workDirectory);

        var rawPath = Path.Combine(workDirectory, "message.eml");

        // Flux systematiquement en using : les flux laisses ouverts sont la
        // cause racine des verrous fichiers de la v1.
        await using (var raw = await source.OpenRawMessageAsync(message, cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(rawPath))
        {
            await raw.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        await using var parseStream = File.OpenRead(rawPath);

        var root = await parser
            .ParseAsync(parseStream, workDirectory, message.Envelope, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return new CaptureContext
        {
            CorrelationId = correlationId,
            MailboxId = configuration.Source.Mailbox,
            ConfigurationName = configuration.Name,
            Identity = message.Identity,
            Envelope = message.Envelope,
            Root = root,
            WorkDirectory = workDirectory,
            RawMessagePath = rawPath,
        };
    }

    /// <summary>Retourne un resultat si le pipeline doit s'arreter, null pour poursuivre.</summary>
    private async Task<CaptureResult?> RunStagesAsync(
        PollerConfiguration configuration,
        CaptureContext context,
        PipelineComponents components,
        CancellationToken cancellationToken)
    {
        var outcome = await RunStageAsync(context, components, ProcessingStage.AfterParse, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return outcome;
        }

        await treeBuilder.ExpandAsync(context, configuration.Extraction.ToLimits(), cancellationToken)
            .ConfigureAwait(false);
        await stateStore.SaveAsync(context, cancellationToken).ConfigureAwait(false);

        outcome = await RunStageAsync(context, components, ProcessingStage.AfterExtract, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return outcome;
        }

        await converterChain.ConvertAsync(context, configuration.Conversion, cancellationToken)
            .ConfigureAwait(false);
        await stateStore.SaveAsync(context, cancellationToken).ConfigureAwait(false);

        outcome = await RunStageAsync(context, components, ProcessingStage.AfterConvert, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return outcome;
        }

        await AssembleAsync(configuration, context, components, cancellationToken).ConfigureAwait(false);

        return await RunStageAsync(context, components, ProcessingStage.BeforeExport, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CaptureResult?> RunStageAsync(
        CaptureContext context,
        PipelineComponents components,
        ProcessingStage stage,
        CancellationToken cancellationToken)
    {
        foreach (var processor in components.Processors
            .Where(p => p.Stages.Contains(stage))
            .OrderBy(p => p.Order))
        {
            var outcome = await processor.ProcessAsync(context, stage, cancellationToken).ConfigureAwait(false);

            switch (outcome.Decision)
            {
                case ProcessingDecision.Continue:
                    continue;

                case ProcessingDecision.Skip:
                    logger.LogInformation(
                        "Message {CorrelationId} ecarte par {Processor} : {Reason}",
                        context.CorrelationId, processor.Name, outcome.Reason);
                    return new CaptureResult(CaptureStatus.Skipped, context.CorrelationId);

                case ProcessingDecision.Reject:
                    logger.LogInformation(
                        "Message {CorrelationId} rejete par {Processor} : {Code} {Reason}",
                        context.CorrelationId, processor.Name, outcome.RejectionCode, outcome.Reason);
                    return new CaptureResult(
                        CaptureStatus.Rejected, context.CorrelationId, outcome.RejectionCode);

                default:
                    continue;
            }
        }

        return null;
    }

    private async Task AssembleAsync(
        PollerConfiguration configuration,
        CaptureContext context,
        PipelineComponents components,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(context.WorkDirectory, "out");
        Directory.CreateDirectory(outputDirectory);

        var baseName = nameResolver.Resolve(configuration.Output.Naming, context);

        if (configuration.Output.PdfMode is PdfOutputMode.Merged or PdfOutputMode.Both)
        {
            var parts = context.Root.PdfParts().ToArray();

            if (parts.Length > 0)
            {
                var mergedPath = Path.Combine(outputDirectory, $"{baseName}.pdf");
                await pdfAssembler.MergeAsync(parts, mergedPath, cancellationToken).ConfigureAwait(false);
                context.Properties["MergedPdf"] = mergedPath;
            }
            else
            {
                // Aucun PDF exploitable : la GED recevra le fichier
                // d'information seul, qui porte le detail des echecs.
                logger.LogWarning(
                    "Aucune piece convertie pour {CorrelationId}, pas de PDF unifie",
                    context.CorrelationId);
            }
        }

        // Les champs personnalises sont resolus une fois, apres conversion et
        // apres les traitements metier : un plugin qui depose un code
        // fournisseur dans Properties doit pouvoir etre repris ici.
        foreach (var (key, value) in fieldResolver.Resolve(
            configuration.Output.Metadata.Fields, context))
        {
            context.CustomFields[key] = value;
        }

        // Reglages destines au writer XSLT, transmis par le sac de proprietes
        // faute de pouvoir etendre IMetadataWriter sans rompre le contrat.
        if (!string.IsNullOrWhiteSpace(configuration.Output.Metadata.Template))
        {
            context.Properties[XsltMetadataWriter.StylesheetKey] =
                configuration.Output.Metadata.Template;
        }

        if (!string.IsNullOrWhiteSpace(configuration.Output.Metadata.Extension))
        {
            context.Properties[XsltMetadataWriter.ExtensionKey] =
                configuration.Output.Metadata.Extension;
        }

        var metadataPath = await components.MetadataWriter
            .WriteAsync(context, outputDirectory, baseName, cancellationToken)
            .ConfigureAwait(false);

        context.Properties["MetadataFile"] = metadataPath;
    }

    private List<ExportItem> BuildExportItems(PollerConfiguration configuration, CaptureContext context)
    {
        var items = new List<ExportItem>();
        var output = configuration.Output;

        if (context.Properties.TryGetValue("MergedPdf", out var merged))
        {
            items.Add(new ExportItem
            {
                LocalPath = merged,
                RelativeName = Path.GetFileName(merged),
                Kind = ExportItemKind.MergedPdf,
            });
        }

        if (output.PdfMode is PdfOutputMode.PerAttachment or PdfOutputMode.Both)
        {
            var index = 0;

            foreach (var node in context.Root.PdfParts())
            {
                var name = nameResolver.Resolve(output.Naming, context, node, index++);

                items.Add(new ExportItem
                {
                    LocalPath = node.PdfPath!,
                    RelativeName = $"{name}.pdf",
                    Kind = ExportItemKind.AttachmentPdf,
                    LogicalPath = node.LogicalPath,
                });
            }
        }

        if (context.Properties.TryGetValue("MetadataFile", out var metadata))
        {
            items.Add(new ExportItem
            {
                LocalPath = metadata,
                RelativeName = Path.GetFileName(metadata),
                Kind = ExportItemKind.Metadata,
            });
        }

        if (configuration.Conversion.KeepOriginals)
        {
            foreach (var node in context.Root.Walk()
                .Where(n => n.SourcePath is not null && n.Kind != DocumentKind.Body))
            {
                items.Add(new ExportItem
                {
                    LocalPath = node.SourcePath!,
                    RelativeName = Path.Combine("originaux", node.FileName),
                    Kind = ExportItemKind.OriginalAttachment,
                    LogicalPath = node.LogicalPath,
                });
            }
        }

        return items;
    }

    private void CleanWorkDirectory(CaptureContext context)
    {
        try
        {
            Directory.Delete(context.WorkDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un nettoyage qui echoue ne remet pas en cause un export reussi.
            // Une tache de maintenance repassera sur les repertoires orphelins.
            logger.LogWarning(ex, "Nettoyage impossible pour {Directory}", context.WorkDirectory);
        }
    }

    private static string BuildCorrelationId(PollerConfiguration configuration, MessageRef message)
    {
        var key = message.Identity.DeduplicationKey;

        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key)));

        // Nom de configuration assaini : il sert de segment de repertoire.
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string([.. configuration.Name.Select(c => invalid.Contains(c) ? '_' : c)]);

        return $"{safeName}_{hash[..16]}";
    }

    private static FolderRef? ToFolderRef(FolderOptions? options) =>
        options is null
            ? null
            : new FolderRef(options.Name ?? options.WellKnown.ToString(), options.WellKnown);
}

/// <summary>
/// Composants resolus pour une configuration donnee : ils dependent des plugins
/// charges et des cibles declarees, donc ils ne peuvent pas etre injectes
/// directement dans le pipeline.
/// </summary>
/// <param name="Processors">Traitements metier actifs, dans l'ordre.</param>
/// <param name="Targets">Cibles d'export avec leur politique effective.</param>
/// <param name="MetadataWriter">Writer du fichier d'information.</param>
public sealed record PipelineComponents(
    IReadOnlyList<ICaptureProcessor> Processors,
    IReadOnlyList<(IExportTarget Target, PolicyOptions Policy)> Targets,
    IMetadataWriter MetadataWriter);
