using System.Collections.Concurrent;
using System.Data;
using ACPoller.Abstractions.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Observability;

/// <summary>
/// Ne collecte rien. Implementation par defaut quand aucune base de metriques
/// n'est configuree.
/// </summary>
/// <remarks>
/// Objet nul plutot que dependance optionnelle : le pipeline appelle
/// <c>Record</c> sans jamais tester la nullite, et l'observabilite s'active
/// par configuration sans toucher au code.
/// </remarks>
public sealed class NullMetricsProvider : IMetricsProvider
{
    /// <inheritdoc />
    public void Record(MetricKind kind, string name, double value, IReadOnlyDictionary<string, string>? tags = null)
    {
        // Volontairement vide.
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Reglages du fournisseur de metriques SQL Server.</summary>
public sealed record SqlMetricsOptions
{
    /// <summary>Chaine de connexion. Vide desactive la collecte.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Table de destination, creee au demarrage si absente.</summary>
    public string TableName { get; init; } = "acpoller_metrics";

    /// <summary>Taille maximale de la file en memoire.</summary>
    public int QueueCapacity { get; init; } = 10000;

    /// <summary>Intervalle d'ecriture par lots, en secondes.</summary>
    public int FlushIntervalSeconds { get; init; } = 30;
}

/// <summary>
/// Enregistre les metriques dans SQL Server, par lots et en arriere-plan.
/// </summary>
/// <remarks>
/// Regle absolue : la collecte ne doit JAMAIS ralentir ni faire echouer le
/// traitement d'un message. <c>Record</c> se contente d'empiler en memoire et
/// retourne immediatement ; une base indisponible fait perdre des metriques,
/// jamais un document.
///
/// La file est bornee : au-dela de la capacite, les nouvelles mesures sont
/// abandonnees. Une base injoignable pendant une nuit ne doit pas consommer
/// toute la memoire du service.
/// </remarks>
public sealed class SqlServerMetricsProvider(
    SqlMetricsOptions options,
    ILogger<SqlServerMetricsProvider> logger) : BackgroundService, IMetricsProvider
{
    private readonly ConcurrentQueue<MetricEntry> _queue = new();
    private int _count;
    private int _dropped;
    private bool _tableReady;

    /// <inheritdoc />
    public void Record(MetricKind kind, string name, double value, IReadOnlyDictionary<string, string>? tags = null)
    {
        if (Volatile.Read(ref _count) >= options.QueueCapacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(new MetricEntry(DateTimeOffset.UtcNow, kind, name, value, FormatTags(tags)));
        Interlocked.Increment(ref _count);
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString) || _queue.IsEmpty)
        {
            return;
        }

        var batch = new List<MetricEntry>();

        while (batch.Count < 1000 && _queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
            Interlocked.Decrement(ref _count);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Les mesures du lot sont perdues, et c'est assume : les remettre en
            // file ferait grossir indefiniment la memoire si la base reste
            // injoignable, et une metrique perdue n'a aucune consequence metier.
            logger.LogWarning(ex, "Ecriture de {Count} metrique(s) impossible", batch.Count);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            logger.LogInformation("Metriques SQL Server desactivees : aucune chaine de connexion");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.FlushIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushAsync(stoppingToken).ConfigureAwait(false);

            var dropped = Interlocked.Exchange(ref _dropped, 0);

            if (dropped > 0)
            {
                // Signale fort : des metriques perdues indiquent soit une base
                // injoignable, soit un volume au-dela du dimensionnement.
                logger.LogWarning("{Count} metrique(s) abandonnee(s), file saturee", dropped);
            }
        }

        // Dernier vidage a l'arret, hors jeton annule.
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteAsync(List<MetricEntry> batch, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        // Copie en masse plutot qu'un INSERT par ligne : sur plusieurs milliers
        // de mesures, l'ecart est d'un facteur cent.
        using var table = new DataTable();
        table.Columns.Add("recorded_utc", typeof(DateTime));
        table.Columns.Add("kind", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("value", typeof(double));
        table.Columns.Add("tags", typeof(string));

        foreach (var entry in batch)
        {
            table.Rows.Add(
                entry.RecordedUtc.UtcDateTime, entry.Kind.ToString(), entry.Name, entry.Value, entry.Tags);
        }

        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = options.TableName };

        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_tableReady)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF OBJECT_ID(N'{options.TableName}', N'U') IS NULL
            CREATE TABLE {options.TableName} (
                id           BIGINT IDENTITY(1,1) PRIMARY KEY,
                recorded_utc DATETIME2      NOT NULL,
                kind         NVARCHAR(20)   NOT NULL,
                name         NVARCHAR(100)  NOT NULL,
                value        FLOAT          NOT NULL,
                tags         NVARCHAR(400)  NULL,
                INDEX ix_metrics_time NONCLUSTERED (recorded_utc, name)
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _tableReady = true;
    }

    private static string? FormatTags(IReadOnlyDictionary<string, string>? tags) =>
        tags is null || tags.Count == 0
            ? null
            : string.Join(';', tags.Select(t => $"{t.Key}={t.Value}"));

    private sealed record MetricEntry(
        DateTimeOffset RecordedUtc, MetricKind Kind, string Name, double Value, string? Tags);
}
