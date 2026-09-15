using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Pipeline;

/// <summary>
/// Depose le lot sur toutes les cibles configurees.
/// </summary>
/// <remarks>
/// En mode <c>AllOrNothing</c>, l'echec d'une cible declenche le rollback des
/// cibles deja ecrites. Une GED qui recoit un message alors qu'une seconde GED
/// ne l'a pas recu produit un ecart de reconciliation que personne ne detecte
/// avant l'audit annuel.
/// </remarks>
public sealed class ExportCoordinator(ILogger<ExportCoordinator> logger)
{
    public async Task<ExportOutcome> ExportAsync(
        CaptureContext context,
        IReadOnlyList<ExportItem> items,
        IReadOnlyList<(IExportTarget Target, PolicyOptions Policy)> targets,
        bool allOrNothing,
        CancellationToken cancellationToken)
    {
        var written = new List<(IExportTarget Target, ExportBatch Batch, IReadOnlyList<string> References)>();
        var failures = new List<Failure>();

        foreach (var (target, policy) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new ExportBatch
            {
                CorrelationId = context.CorrelationId,
                Context = context,
                Items = items,
                Policy = ToPolicy(policy),
            };

            var result = await ExportWithRetryAsync(target, batch, policy, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                written.Add((target, batch, result.WrittenReferences));
                continue;
            }

            var failure = result.Failure ?? Failure.Permanent($"Export echoue sur '{target.Name}'.");
            failures.Add(failure);

            logger.LogError(
                "Export {Target} echoue pour {CorrelationId} : {Reason}",
                target.Name,
                context.CorrelationId,
                failure.Reason);

            if (allOrNothing)
            {
                await RollbackAsync(written, cancellationToken).ConfigureAwait(false);
                return new ExportOutcome(false, failures, []);
            }
        }

        var references = written.SelectMany(w => w.References).ToArray();
        return new ExportOutcome(failures.Count == 0 || !allOrNothing, failures, references);
    }

    private async Task<ExportResult> ExportWithRetryAsync(
        IExportTarget target,
        ExportBatch batch,
        PolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(policy.InitialBackoffSeconds);

        for (var attempt = 1; ; attempt++)
        {
            ExportResult result;

            try
            {
                result = await target.ExportAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AcPollerException ex)
            {
                result = ExportResult.Ko(ex.Failure);
            }
            catch (Exception ex)
            {
                // Une exception non qualifiee est traitee comme transitoire :
                // en cas de doute, reessayer coute moins cher que perdre.
                result = ExportResult.Ko(Failure.Transient(ex.Message, exception: ex));
            }

            if (result.Success)
            {
                return result;
            }

            // Le type de l'echec decide, pas une heuristique sur le message.
            if (result.Failure?.IsTransient != true || attempt >= policy.MaxAttempts)
            {
                return result;
            }

            logger.LogWarning(
                "Export {Target} tentative {Attempt}/{Max} echouee, nouvelle tentative dans {Delay}",
                target.Name,
                attempt,
                policy.MaxAttempts,
                delay);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay *= 2;
        }
    }

    private async Task RollbackAsync(
        List<(IExportTarget Target, ExportBatch Batch, IReadOnlyList<string> References)> written,
        CancellationToken cancellationToken)
    {
        foreach (var (target, batch, references) in Enumerable.Reverse(written))
        {
            try
            {
                await target.RollbackAsync(batch, references, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Rollback effectue sur {Target}", target.Name);
            }
            catch (Exception ex)
            {
                // Un rollback qui echoue laisse un depot orphelin. On le signale
                // fort : c'est une intervention manuelle, pas un simple incident.
                logger.LogCritical(
                    ex,
                    "ROLLBACK IMPOSSIBLE sur {Target} pour {CorrelationId}. Depot partiel a nettoyer manuellement : {References}",
                    target.Name,
                    batch.CorrelationId,
                    string.Join(", ", references));
            }
        }
    }

    private static ExportPolicy ToPolicy(PolicyOptions options) => new()
    {
        AtomicWrite = options.AtomicWrite,
        SentinelFileName = options.SentinelFileName,
        AllOrNothing = options.AllOrNothing,
        MaxAttempts = options.MaxAttempts,
        InitialBackoff = TimeSpan.FromSeconds(options.InitialBackoffSeconds),
    };
}

public sealed record ExportOutcome(
    bool Success,
    IReadOnlyList<Failure> Failures,
    IReadOnlyList<string> WrittenReferences);
