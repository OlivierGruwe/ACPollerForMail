using ACPoller.Abstractions.Infrastructure;
using ACPoller.Abstractions.Sources;
using ACPoller.Core.Configuration;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace ACPoller.Core.Workers;

/// <summary>Etat courant d'un worker, expose a la supervision.</summary>
public enum WorkerState
{
    /// <summary>Cree, pas encore demarre.</summary>
    Created = 0,

    /// <summary>En attente du prochain cycle.</summary>
    Idle = 1,

    /// <summary>Cycle en cours.</summary>
    Running = 2,

    /// <summary>Arrete apres une erreur non recuperable.</summary>
    Faulted = 3,

    /// <summary>Arrete a la demande.</summary>
    Stopped = 4,
}

/// <summary>
/// Collecte et traite les messages d'une boite, en boucle.
/// </summary>
/// <remarks>
/// Le jeton d'annulation du worker est DISTINCT de celui du service : redemarrer
/// une seule boite, depuis l'UI ou apres une erreur, ne doit pas arreter les
/// cent autres. C'est le decouplage qui manquait en v1.
/// </remarks>
public sealed class MailboxWorker(
    PollerConfiguration configuration,
    RuntimeFactory runtimeFactory,
    CapturePipeline pipeline,
    IProcessedStore processedStore,
    ICaptureStateStore stateStore,
    GlobalConcurrencyLimiter limiter,
    IMetricsProvider metrics,
    ILogger<MailboxWorker> logger)
{
    /// <summary>Nom de la configuration servie par ce worker.</summary>
    public string Name => configuration.Name;

    /// <summary>Etat courant, pour la supervision.</summary>
    public WorkerState State { get; private set; } = WorkerState.Created;

    /// <summary>Fin du dernier cycle acheve, en UTC.</summary>
    public DateTimeOffset? LastCycleUtc { get; private set; }

    /// <summary>Derniere erreur rencontree, affichable dans l'UI.</summary>
    public string? LastError { get; private set; }

    /// <summary>Boucle de collecte, jusqu'a annulation.</summary>
    /// <param name="cancellationToken">Jeton propre a ce worker.</param>
    /// <returns>Une tache achevee a l'arret du worker.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Decalage aleatoire : sans lui, cent workers frappent Graph a la meme
        // seconde a chaque demarrage du service, et le throttling se declenche
        // avant meme le premier message.
        var jitter = Random.Shared.Next(0, configuration.Schedule.StartupJitterSeconds + 1);
        await Task.Delay(TimeSpan.FromSeconds(jitter), cancellationToken).ConfigureAwait(false);

        await ResumePendingAsync(cancellationToken).ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(configuration.Schedule.IntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                State = WorkerState.Running;
                await RunCycleAsync(cancellationToken).ConfigureAwait(false);
                LastCycleUtc = DateTimeOffset.UtcNow;
                LastError = null;
                State = WorkerState.Idle;
            }
            catch (OperationCanceledException)
            {
                State = WorkerState.Stopped;
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                // Injoignable : incident d'exploitation courant, pas un defaut
                // du service. Message seul, la pile n'apporte rien et pollue
                // le fichier de log.
                LastError = ex.Message;
                State = WorkerState.Idle;
                logger.LogWarning(
                    "{Configuration} : source injoignable ({Reason})", configuration.Name, ex.Message);
                metrics.Record(MetricKind.Counter, "worker.unreachable", 1,
                    new Dictionary<string, string> { ["configuration"] = configuration.Name });
            }
            catch (Exception ex)
            {
                // Un cycle en erreur n'arrete pas le worker : la cause est
                // souvent transitoire (reseau, GED indisponible) et le cycle
                // suivant reprendra les messages non traites.
                LastError = ex.Message;
                State = WorkerState.Idle;
                logger.LogError(ex, "Cycle en erreur pour {Configuration}", configuration.Name);
                metrics.Record(MetricKind.Counter, "worker.cycle_error", 1,
                    new Dictionary<string, string> { ["configuration"] = configuration.Name });
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        State = WorkerState.Stopped;
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var source = runtimeFactory.CreateSource(configuration);
        var components = runtimeFactory.CreateComponents(configuration);

        try
        {
            var folder = await source.ResolveFolderAsync(ToFolderRef(configuration.Source.Folder), cancellationToken)
                .ConfigureAwait(false);

            var lookback = LastCycleUtc is null && configuration.Query.InitialLookbackDays > 0
                ? DateTimeOffset.UtcNow.AddDays(-configuration.Query.InitialLookbackDays)
                : (DateTimeOffset?)null;

            // Le plancher absolu prime toujours sur le rattrapage relatif.
            var since = configuration.Query.NotBefore is { } floor
                ? (lookback is { } l && l > floor ? l : floor)
                : lookback;

            var query = new MessageQuery
            {
                UnreadOnly = configuration.Query.UnreadOnly,
                MaxCount = configuration.Query.MaxCount,
                RequireAttachments = configuration.Query.RequireAttachments,
                Since = since,
            };

            var processed = 0;

            await foreach (var message in source.ListAsync(folder, query, cancellationToken).ConfigureAwait(false))
            {
                // Le controle d'idempotence a lieu AVANT le telechargement :
                // inutile de ramener plusieurs mega-octets pour les jeter ensuite.
                if (await processedStore.IsProcessedAsync(
                        configuration.Source.Mailbox,
                        message.Identity.DeduplicationKey,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var result = await limiter.RunAsync(
                    ct => pipeline.ProcessAsync(configuration, source, message, components, ct),
                    cancellationToken).ConfigureAwait(false);

                metrics.Record(MetricKind.Counter, $"capture.{result.Status}".ToLowerInvariant(), 1,
                    new Dictionary<string, string> { ["configuration"] = configuration.Name });

                processed++;
            }

            if (processed > 0)
            {
                logger.LogInformation(
                    "{Configuration} : {Count} message(s) traite(s)",
                    configuration.Name,
                    processed);
            }
        }
        finally
        {
            foreach (var (target, _) in components.Targets)
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ResumePendingAsync(CancellationToken cancellationToken)
    {
        var pending = 0;

        await foreach (var context in stateStore
            .LoadPendingAsync(configuration.Source.Mailbox, cancellationToken)
            .ConfigureAwait(false))
        {
            pending++;
            logger.LogInformation(
                "Unite de travail inachevee detectee : {CorrelationId} (tentative {Attempt})",
                context.CorrelationId,
                context.Attempt);
        }

        if (pending > 0)
        {
            // Les contextes sont repris naturellement au prochain passage du
            // message dans le cycle : le pipeline recharge l'etat par son
            // identifiant de correlation et saute ce qui est deja fait.
            logger.LogInformation(
                "{Configuration} : {Count} unite(s) de travail seront reprises",
                configuration.Name,
                pending);
        }
    }

    private static FolderRef ToFolderRef(FolderOptions options) =>
        new(options.Name ?? options.WellKnown.ToString(), options.WellKnown);
}
