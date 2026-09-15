using ACPoller.Core.Configuration;
using ACPoller.Core.Observability;
using ACPoller.Core.Workers;
using ACPoller.Sources.Graph;
using Microsoft.Extensions.Options;

namespace ACPoller.Service.Control;

/// <summary>
/// Points d'entree du tableau de bord.
/// </summary>
/// <remarks>
/// Une seule requete renvoie tout ce que l'ecran affiche. C'est deliberе :
/// l'interface interroge toutes les dix secondes, et multiplier les
/// aller-retours pour un ecran unique n'apporte que de la latence et des etats
/// partiellement rafraichis, ou les compteurs et les workers ne correspondent
/// pas au meme instant.
/// </remarks>
public static class DashboardEndpoints
{
    /// <summary>Declare les points d'entree du tableau de bord.</summary>
    /// <param name="group">Groupe de routes deja protege par le filtre de jeton.</param>
    /// <returns>Le groupe, pour le chainage.</returns>
    public static RouteGroupBuilder MapDashboardEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/dashboard", GetDashboard);
        group.MapPost("/dashboard/reset", ResetMetrics);

        return group;
    }

    private static IResult GetDashboard(
        MetricsSnapshotProvider metrics,
        PollerHostedService workers,
        GlobalDiskInfo disk,
        IOptions<PollerOptions> options)
    {
        var snapshot = metrics.GetSnapshot();

        double Sum(string name) =>
            snapshot.Aggregates.FirstOrDefault(a => a.Name == name)?.Sum ?? 0;

        var exported = Sum("capture.exported");
        var failed = Sum("capture.failed");
        var rejected = Sum("capture.rejected");
        var skipped = Sum("capture.skipped");

        var duration = snapshot.Aggregates.FirstOrDefault(a => a.Name == "capture.duration_ms");

        var dashboard = new DashboardDto
        {
            SinceUtc = snapshot.SinceUtc,

            Exported = (long)exported,
            Failed = (long)failed,
            Rejected = (long)rejected,
            Skipped = (long)skipped,

            // Taux calcule sur les messages REELLEMENT traites : inclure les
            // ecartes fausserait la lecture sur un flux ou un plugin metier
            // filtre la majorite des messages.
            SuccessRate = exported + failed + rejected > 0
                ? Math.Round(exported * 100 / (exported + failed + rejected), 1)
                : 100,

            AverageDurationMs = (int)(duration?.Average ?? 0),
            P95DurationMs = (int)(duration?.P95 ?? 0),

            ThrottleEvents = GraphThrottlingHandler.ThrottleCount,
            ConversionFailures = (long)Sum("conversion.failed"),
            CycleErrors = (long)Sum("worker.cycle_error"),
            UnreachableCycles = (long)Sum("worker.unreachable"),

            FreeDiskPercent = disk.GetFreeSpacePercent(options.Value.WorkDirectory),
            MaxGlobalConcurrency = options.Value.MaxGlobalConcurrency,
            

            Workers =
            [
                .. workers.Workers.Select(w => new WorkerStatus
                {
                    Name = w.Name,
                    State = w.State.ToString(),
                    LastCycleUtc = w.LastCycleUtc,
                    LastError = w.LastError,
                })
            ],

            // Ventilation par boite, triee par nombre d'echecs : c'est la
            // premiere chose qu'on cherche sur un ecran d'exploitation.
            ByConfiguration =
            [
                .. snapshot.ByConfiguration
                    .Select(c => new ConfigurationMetrics
                    {
                        Name = c.Key,
                        Exported = (long)c.Value.GetValueOrDefault("capture.exported"),
                        Failed = (long)c.Value.GetValueOrDefault("capture.failed"),
                        Rejected = (long)c.Value.GetValueOrDefault("capture.rejected"),
                        AverageDurationMs = c.Value.TryGetValue("capture.duration_ms", out var total)
                            && c.Value.GetValueOrDefault("capture.exported") > 0
                                ? (int)(total / c.Value["capture.exported"])
                                : 0,
                    })
                    .OrderByDescending(c => c.Failed)
                    .ThenByDescending(c => c.Exported)
            ],

            HourlyExported = snapshot.Hourly.TryGetValue("capture.exported", out var hourly)
                ? [.. hourly.Select(h => new HourlyPoint { HourUtc = h.HourUtc, Value = h.Value })]
                : [],
        };

        return Results.Ok(dashboard);
    }

    private static IResult ResetMetrics(MetricsSnapshotProvider metrics)
    {
        // Utile avant un test de charge ou apres correction d'un incident :
        // repartir de compteurs propres evite de lire des chiffres pollues par
        // ce qu'on vient justement de corriger.
        metrics.Reset();

        return Results.Ok(new { message = "Compteurs remis a zero." });
    }
}

/// <summary>Donnees du tableau de bord.</summary>
public sealed record DashboardDto
{
    /// <summary>Debut de la fenetre observee.</summary>
    public DateTimeOffset SinceUtc { get; init; }

    /// <summary>Messages exportes avec succes.</summary>
    public long Exported { get; init; }

    /// <summary>Messages en echec.</summary>
    public long Failed { get; init; }

    /// <summary>Messages rejetes par un traitement metier.</summary>
    public long Rejected { get; init; }

    /// <summary>Messages ecartes par un traitement metier.</summary>
    public long Skipped { get; init; }

    /// <summary>Taux de succes, en pourcentage.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Duree moyenne de traitement.</summary>
    public int AverageDurationMs { get; init; }

    /// <summary>95e centile de la duree, plus parlant que la moyenne.</summary>
    public int P95DurationMs { get; init; }

    /// <summary>Nombre de limitations Graph rencontrees.</summary>
    public long ThrottleEvents { get; init; }

    /// <summary>Pieces non converties.</summary>
    public long ConversionFailures { get; init; }

    /// <summary>Cycles termines en erreur.</summary>
    public long CycleErrors { get; init; }

    /// <summary>Cycles ou la source etait injoignable.</summary>
    public long UnreachableCycles { get; init; }

    /// <summary>Espace libre sur le volume de travail.</summary>
    public int? FreeDiskPercent { get; init; }

    /// <summary>Concurrence globale configuree.</summary>
    public int MaxGlobalConcurrency { get; init; }

    /// <summary>Etat de chaque worker.</summary>
    public IReadOnlyList<WorkerStatus> Workers { get; init; } = [];

    /// <summary>Ventilation par configuration, la plus en echec en tete.</summary>
    public IReadOnlyList<ConfigurationMetrics> ByConfiguration { get; init; } = [];

    /// <summary>Volume exporte par heure.</summary>
    public IReadOnlyList<HourlyPoint> HourlyExported { get; init; } = [];
}

/// <summary>Metriques d'une configuration.</summary>
public sealed record ConfigurationMetrics
{
    /// <summary>Nom de la configuration.</summary>
    public required string Name { get; init; }

    /// <summary>Messages exportes.</summary>
    public long Exported { get; init; }

    /// <summary>Messages en echec.</summary>
    public long Failed { get; init; }

    /// <summary>Messages rejetes.</summary>
    public long Rejected { get; init; }

    /// <summary>Duree moyenne de traitement.</summary>
    public int AverageDurationMs { get; init; }
}

/// <summary>Un point de l'historique horaire.</summary>
public sealed record HourlyPoint
{
    /// <summary>Debut de l'heure, en UTC.</summary>
    public DateTimeOffset HourUtc { get; init; }

    /// <summary>Valeur cumulee sur l'heure.</summary>
    public double Value { get; init; }
}
