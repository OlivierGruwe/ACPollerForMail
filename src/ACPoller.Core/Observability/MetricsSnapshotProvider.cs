using System.Collections.Concurrent;
using ACPoller.Abstractions.Infrastructure;

namespace ACPoller.Core.Observability;

/// <summary>Valeurs agregees d'une metrique sur la fenetre observee.</summary>
/// <param name="Name">Nom de la metrique.</param>
/// <param name="Count">Nombre de mesures.</param>
/// <param name="Sum">Somme des valeurs.</param>
/// <param name="Average">Moyenne.</param>
/// <param name="Min">Valeur minimale.</param>
/// <param name="Max">Valeur maximale.</param>
/// <param name="P95">95e centile, plus parlant que la moyenne sur des durees.</param>
public sealed record MetricAggregate(
    string Name,
    long Count,
    double Sum,
    double Average,
    double Min,
    double Max,
    double P95);

/// <summary>Valeur d'une metrique sur un intervalle horaire.</summary>
/// <param name="HourUtc">Debut de l'heure, en UTC.</param>
/// <param name="Value">Somme des mesures de l'heure.</param>
public sealed record MetricBucket(DateTimeOffset HourUtc, double Value);

/// <summary>Instantane des metriques, tel qu'expose au tableau de bord.</summary>
public sealed record MetricsSnapshot
{
    /// <summary>Debut de la fenetre observee.</summary>
    public required DateTimeOffset SinceUtc { get; init; }

    /// <summary>Agregats par metrique, sur toute la fenetre.</summary>
    public IReadOnlyList<MetricAggregate> Aggregates { get; init; } = [];

    /// <summary>Compteurs par configuration : nom, metrique, valeur.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ByConfiguration { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    /// <summary>Historique horaire des metriques suivies dans le temps.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MetricBucket>> Hourly { get; init; } =
        new Dictionary<string, IReadOnlyList<MetricBucket>>();
}

/// <summary>
/// Agrege les metriques en memoire pour le tableau de bord.
/// </summary>
/// <remarks>
/// Decorateur autour du fournisseur reel : les mesures continuent d'aller vers
/// SQL Server ou vers l'objet nul, et sont EN PLUS agregees ici. Le tableau de
/// bord fonctionne donc meme sans base de metriques configuree, ce qui est le
/// cas le plus frequent en debut de projet.
///
/// Fenetre glissante de vingt-quatre heures, en memoire uniquement. Ce n'est
/// pas un entrepot : pour l'historique long, c'est la base SQL qui sert. Cette
/// agregation repond a une seule question, celle qu'on se pose devant un
/// ecran d'exploitation : est-ce que ca tourne normalement en ce moment.
/// </remarks>
public sealed class MetricsSnapshotProvider(IMetricsProvider inner) : IMetricsProvider
{
    private const int WindowHours = 24;

    // Bornes de securite : un service qui tourne des mois ne doit pas voir sa
    // memoire croitre avec le nombre de noms de metriques distincts.
    private const int MaxSamplesPerMetric = 5000;
    private const int MaxTrackedMetrics = 200;

    private readonly ConcurrentDictionary<string, MetricSeries> _series = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, double>> _byConfiguration =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public void Record(MetricKind kind, string name, double value, IReadOnlyDictionary<string, string>? tags = null)
    {
        inner.Record(kind, name, value, tags);

        if (_series.Count < MaxTrackedMetrics || _series.ContainsKey(name))
        {
            _series.GetOrAdd(name, _ => new MetricSeries()).Add(value);
        }

        // Ventilation par configuration : c'est la seule etiquette qui compte
        // pour un exploitant, celle qui repond a "quelle boite pose probleme".
        if (tags?.TryGetValue("configuration", out var configuration) == true)
        {
            var perConfiguration = _byConfiguration.GetOrAdd(
                configuration, _ => new ConcurrentDictionary<string, double>(StringComparer.Ordinal));

            perConfiguration.AddOrUpdate(name, value, (_, current) => current + value);
        }
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <summary>Produit l'instantane courant.</summary>
    /// <returns>Les metriques agregees sur la fenetre observee.</returns>
    public MetricsSnapshot GetSnapshot()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-WindowHours);

        var aggregates = _series
            .Select(s => s.Value.Aggregate(s.Key, cutoff))
            .Where(a => a.Count > 0)
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();

        var hourly = _series.ToDictionary(
            s => s.Key,
            s => s.Value.Hourly(cutoff),
            StringComparer.Ordinal);

        var byConfiguration = _byConfiguration.ToDictionary(
            c => c.Key,
            c => (IReadOnlyDictionary<string, double>)new Dictionary<string, double>(c.Value),
            StringComparer.OrdinalIgnoreCase);

        return new MetricsSnapshot
        {
            SinceUtc = _startedUtc > cutoff ? _startedUtc : cutoff,
            Aggregates = aggregates,
            ByConfiguration = byConfiguration,
            Hourly = hourly.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>Remet les compteurs a zero. Declenche depuis l'interface.</summary>
    public void Reset()
    {
        _series.Clear();
        _byConfiguration.Clear();
    }

    /// <summary>
    /// Serie d'une metrique : echantillons horodates, sous verrou.
    /// </summary>
    /// <remarks>
    /// Verrou plutot que collection concurrente : l'agregation doit voir un
    /// etat coherent, et la duree de detention est de l'ordre de la
    /// microseconde. Une file concurrente obligerait a copier avant de calculer,
    /// pour un gain nul a ce volume.
    /// </remarks>
    private sealed class MetricSeries
    {
        private readonly Lock _gate = new();
        private readonly Queue<(DateTimeOffset At, double Value)> _samples = new();

        public void Add(double value)
        {
            lock (_gate)
            {
                _samples.Enqueue((DateTimeOffset.UtcNow, value));

                // Purge par le nombre ET par l'age : un flux intense sature la
                // premiere borne, un flux lent laisserait sinon des mesures de
                // la semaine derniere fausser les moyennes.
                var cutoff = DateTimeOffset.UtcNow.AddHours(-WindowHours);

                while (_samples.Count > MaxSamplesPerMetric
                    || (_samples.Count > 0 && _samples.Peek().At < cutoff))
                {
                    _samples.Dequeue();
                }
            }
        }

        public MetricAggregate Aggregate(string name, DateTimeOffset cutoff)
        {
            double[] values;

            lock (_gate)
            {
                values = [.. _samples.Where(s => s.At >= cutoff).Select(s => s.Value)];
            }

            if (values.Length == 0)
            {
                return new MetricAggregate(name, 0, 0, 0, 0, 0, 0);
            }

            Array.Sort(values);

            // 95e centile plutot que la seule moyenne : sur des durees de
            // traitement, la moyenne masque les cas lents, et ce sont eux qui
            // saturent les slots de concurrence.
            var index = (int)Math.Ceiling(values.Length * 0.95) - 1;
            var p95 = values[Math.Clamp(index, 0, values.Length - 1)];

            return new MetricAggregate(
                name,
                values.Length,
                values.Sum(),
                values.Average(),
                values[0],
                values[^1],
                p95);
        }

        public IReadOnlyList<MetricBucket> Hourly(DateTimeOffset cutoff)
        {
            (DateTimeOffset At, double Value)[] samples;

            lock (_gate)
            {
                samples = [.. _samples.Where(s => s.At >= cutoff)];
            }

            return
            [
                .. samples
                    .GroupBy(s => new DateTimeOffset(
                        s.At.Year, s.At.Month, s.At.Day, s.At.Hour, 0, 0, TimeSpan.Zero))
                    .Select(g => new MetricBucket(g.Key, g.Sum(s => s.Value)))
                    .OrderBy(b => b.HourUtc)
            ];
        }
    }
}
