using ACPoller.Abstractions.Infrastructure;
using ACPoller.Core.Configuration;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ACPoller.Core.Workers;

/// <summary>
/// Demarre un worker par configuration active et supervise leur cycle de vie.
/// </summary>
/// <remarks>
/// Chaque worker possede sa propre source d'annulation, chainee a celle du
/// service. Consequence : arreter ou redemarrer une boite depuis l'UI n'impacte
/// aucune autre, et une boite en erreur permanente ne fait pas tomber le service.
/// </remarks>
public sealed class PollerHostedService(
    IOptions<PollerOptions> options,
    IConfiguration configuration,
    ConfigurationResolver resolver,
    ConfigurationValidator validator,
    RuntimeFactory runtimeFactory,
    CapturePipeline pipeline,
    IProcessedStore processedStore,
    ICaptureStateStore stateStore,
    IMetricsProvider metrics,
    ILoggerFactory loggerFactory,
    ILogger<PollerHostedService> logger) : BackgroundService
{
    private readonly Dictionary<string, WorkerHandle> _workers = new(StringComparer.OrdinalIgnoreCase);
    private GlobalConcurrencyLimiter? _limiter;

    /// <summary>Etat des workers, pour la supervision depuis l'UI.</summary>
    public IReadOnlyCollection<MailboxWorker> Workers =>
        [.. _workers.Values.Select(w => w.Worker)];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configurations = resolver.Resolve(configuration);

        var issues = validator.Validate(configurations);

        foreach (var issue in issues.Where(i => !i.IsBlocking))
        {
            logger.LogWarning(
                "{Configuration} [{Path}] : {Message}",
                issue.ConfigurationName, issue.Path, issue.Message);
        }

        var blocking = issues.Where(i => i.IsBlocking).ToArray();
        foreach (var issue in blocking)
        {
            logger.LogError(
                "{Configuration} [{Path}] : {Message}",
                issue.ConfigurationName, issue.Path, issue.Message);
        }

        // Une configuration invalide est ecartee, les autres demarrent. Refuser
        // de demarrer le service entier parce qu'une boite sur cent est mal
        // renseignee serait disproportionne.
        var invalid = blocking.Select(i => i.ConfigurationName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _limiter = new GlobalConcurrencyLimiter(options.Value.MaxGlobalConcurrency);

        foreach (var config in configurations.Where(c => c.Enabled && !invalid.Contains(c.Name)))
        {
            StartWorker(config, stoppingToken);
        }

        logger.LogInformation(
            "{Started} worker(s) demarre(s), {Skipped} ecarte(s), concurrence globale {Concurrency}",
            _workers.Count,
            configurations.Count - _workers.Count,
            options.Value.MaxGlobalConcurrency);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arret normal du service.
        }
        finally
        {
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Redemarre un worker sans toucher aux autres.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="stoppingToken">Jeton du service.</param>
    /// <returns>Une tache achevee quand le worker est relance.</returns>
    public async Task RestartWorkerAsync(string name, CancellationToken stoppingToken)
    {
        if (_workers.Remove(name, out var handle))
        {
            await handle.StopAsync().ConfigureAwait(false);
        }

        var configurations = resolver.Resolve(configuration);
        var config = configurations.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            logger.LogWarning("Redemarrage impossible : configuration {Name} introuvable", name);
            return;
        }

        StartWorker(config, stoppingToken);
        logger.LogInformation("Worker {Name} redemarre", name);
    }

    private void StartWorker(PollerConfiguration config, CancellationToken stoppingToken)
    {
        var worker = new MailboxWorker(
            config,
            runtimeFactory,
            pipeline,
            processedStore,
            stateStore,
            _limiter!,
            metrics,
            loggerFactory.CreateLogger<MailboxWorker>());

        // Source chainee : l'arret du service arrete tout le monde, mais l'arret
        // d'un worker ne remonte jamais vers le service.
        // Source chainee : l'arret du service arrete tout le monde, mais l'arret
        // d'un worker ne remonte jamais vers le service.
        // CA2000 : la propriete est transferee a WorkerHandle, qui libere la
        // source dans StopAsync. L'analyseur ne sait pas suivre ce transfert.
#pragma warning disable CA2000
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
#pragma warning restore CA2000

        var task = Task.Run(async () =>
        {
            try
            {
                await worker.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arret demande.
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Worker {Name} arrete sur erreur non recuperable", config.Name);
            }
        }, CancellationToken.None);

        _workers[config.Name] = new WorkerHandle(worker, cts, task);
    }

    private async Task StopAllAsync()
    {
        foreach (var handle in _workers.Values)
        {
            await handle.StopAsync().ConfigureAwait(false);
        }

        _workers.Clear();
        _limiter?.Dispose();
    }

    private sealed record WorkerHandle(MailboxWorker Worker, CancellationTokenSource Cts, Task Task)
    {
        public async Task StopAsync()
        {
            await Cts.CancelAsync().ConfigureAwait(false);

            try
            {
                // Delai borne : un worker bloque sur une conversion ne doit pas
                // retarder indefiniment l'arret du service.
                await Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // L'arret se poursuit malgre tout.
            }
            catch (OperationCanceledException)
            {
                // Attendu.
            }

            Cts.Dispose();
        }
    }
}
