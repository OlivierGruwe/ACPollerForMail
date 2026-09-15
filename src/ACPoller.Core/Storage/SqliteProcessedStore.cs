using ACPoller.Abstractions.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Storage;

/// <summary>
/// Journal des messages traites, sur SQLite.
/// </summary>
/// <remarks>
/// SQLite plutot qu'un fichier texte par boite : avec une centaine de boites et
/// des centaines de milliers d'entrees, la relecture integrale d'un fichier a
/// chaque controle d'idempotence devient le poste de cout dominant. Ici, un
/// index compose repond en temps constant.
///
/// Le fichier est local au service et n'est jamais partage entre instances :
/// c'est ce qui rend SQLite acceptable ici, malgre l'ecriture concurrente.
/// </remarks>
public sealed class SqliteProcessedStore : IProcessedStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteProcessedStore> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    /// <summary>Construit le journal.</summary>
    /// <param name="databasePath">Chemin du fichier SQLite. Le repertoire est cree si besoin.</param>
    /// <param name="logger">Journalisation.</param>
    public SqliteProcessedStore(string databasePath, ILogger<SqliteProcessedStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsProcessedAsync(
        string mailboxId,
        string deduplicationKey,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM processed WHERE mailbox = $mailbox AND dedup_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$mailbox", mailboxId);
        command.Parameters.AddWithValue("$key", deduplicationKey);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    /// <inheritdoc />
    public async Task MarkProcessedAsync(
        string mailboxId,
        string deduplicationKey,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // INSERT OR IGNORE : marquer deux fois le meme message n'est pas une
        // erreur. Cela arrive legitimement apres une reprise, et faire echouer
        // le traitement a ce stade rejouerait un export deja reussi.
        command.CommandText = """
            INSERT OR IGNORE INTO processed (mailbox, dedup_key, processed_utc)
            VALUES ($mailbox, $key, $utc);
            """;
        command.Parameters.AddWithValue("$mailbox", mailboxId);
        command.Parameters.AddWithValue("$key", deduplicationKey);
        command.Parameters.AddWithValue("$utc", processedUtc.UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PurgeOlderThanAsync(
        string mailboxId,
        DateTimeOffset threshold,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM processed WHERE mailbox = $mailbox AND processed_utc < $threshold;";
        command.Parameters.AddWithValue("$mailbox", mailboxId);
        command.Parameters.AddWithValue("$threshold", threshold.UtcDateTime);

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("{Count} entree(s) purgee(s) pour {Mailbox}", deleted, mailboxId);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> ImportLegacyAsync(
        string mailboxId,
        string legacyFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(legacyFilePath))
        {
            _logger.LogInformation("Aucun fichier historique a importer : {Path}", legacyFilePath);
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Transaction unique : sans elle, l'import de plusieurs dizaines de
        // milliers de lignes provoque autant de synchronisations disque.
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO processed (mailbox, dedup_key, processed_utc)
            VALUES ($mailbox, $key, $utc);
            """;

        var mailboxParameter = command.Parameters.Add("$mailbox", SqliteType.Text);
        var keyParameter = command.Parameters.Add("$key", SqliteType.Text);
        var utcParameter = command.Parameters.Add("$utc", SqliteType.Text);

        mailboxParameter.Value = mailboxId;

        // Horodatage d'import et non d'origine : la v1 ne conservait pas la date
        // de traitement. La retention repartira donc de zero pour ces entrees,
        // ce qui est le comportement sur, pas le comportement exact.
        utcParameter.Value = DateTime.UtcNow;

        var imported = 0;

        await foreach (var line in File.ReadLinesAsync(legacyFilePath, cancellationToken).ConfigureAwait(false))
        {
            var key = line.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            keyParameter.Value = key;
            imported += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{Count} entree(s) importee(s) depuis {Path} pour {Mailbox}",
            imported, legacyFilePath, mailboxId);

        return imported;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();

            // WAL : lectures concurrentes non bloquantes pendant les ecritures.
            // Indispensable avec plusieurs workers actifs en parallele.
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS processed (
                    mailbox      TEXT NOT NULL,
                    dedup_key    TEXT NOT NULL,
                    processed_utc TEXT NOT NULL,
                    PRIMARY KEY (mailbox, dedup_key)
                );

                CREATE INDEX IF NOT EXISTS ix_processed_purge
                    ON processed (mailbox, processed_utc);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _initGate.Dispose();

        // Libere les connexions du pool et le verrou sur le fichier : sans cela,
        // le fichier reste verrouille apres l'arret du service.
        SqliteConnection.ClearAllPools();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
