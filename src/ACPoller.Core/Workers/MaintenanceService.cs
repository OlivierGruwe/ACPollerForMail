using ACPoller.Abstractions.Infrastructure;
using ACPoller.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace ACPoller.Core.Workers;

/// <summary>Reglages de la maintenance.</summary>
public sealed record MaintenanceOptions
{
    /// <summary>Intervalle entre deux passes, en heures.</summary>
    public int IntervalHours { get; init; } = 6;

    /// <summary>
    /// Age minimal d'un repertoire avant qu'il soit seulement CONSIDERE, en heures.
    /// Protege contre la suppression d'un travail en cours : aucun message ne
    /// doit rester en traitement plus longtemps que cette duree.
    /// </summary>
    public int MinimumAgeHours { get; init; } = 6;

    /// <summary>
    /// Age au-dela duquel une unite de travail inachevee part en quarantaine.
    /// Passe ce delai, la reprise n'a plus de sens : soit le message a disparu
    /// de la boite, soit l'echec est structurel.
    /// </summary>
    public int QuarantineAfterDays { get; init; } = 7;

    /// <summary>Age au-dela duquel une quarantaine est supprimee, en jours.</summary>
    public int QuarantineRetentionDays { get; init; } = 30;

    /// <summary>
    /// Seuil d'alerte sur l'espace disque libre, en pourcentage. Une alerte
    /// avant saturation vaut mieux qu'un service arrete un dimanche.
    /// </summary>
    public int MinFreeDiskPercent { get; init; } = 10;
}

/// <summary>
/// Purge les repertoires de travail et le journal des messages traites.
/// </summary>
/// <remarks>
/// Sans cette tache, un flux qui echoue regulierement remplit le disque, et
/// personne ne le voit venir. C'est le mode de defaillance le plus previsible
/// d'un service de capture, et le plus penible : il se declenche un dimanche,
/// il arrete TOUTES les boites, et le diagnostic prend dix minutes pendant que
/// le stress en prend soixante.
///
/// Principe de precaution central : rien n'est supprime sans avoir ete d'abord
/// mis en quarantaine, et rien n'est touche avant un age minimal qui exclut
/// tout travail en cours. Un repertoire supprime a tort, c'est un message
/// perdu sans trace.
/// </remarks>
public sealed class MaintenanceService(
    IOptions<PollerOptions> pollerOptions,
    IOptions<MaintenanceOptions> maintenanceOptions,
    IProcessedStore processedStore,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private const string QuarantineFolderName = "_quarantaine";
    private const string StateFolderName = "state";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = maintenanceOptions.Value;

        // Premiere passe au demarrage : c'est souvent apres un incident que le
        // service redemarre, donc au moment ou le menage est le plus utile.
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.IntervalHours));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Execute une passe complete. Publique pour un declenchement manuel depuis l'UI.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee a la fin de la passe.</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            PurgeConfigurationBackups();
            CheckDiskSpace();
            await QuarantineStaleWorkAsync(cancellationToken).ConfigureAwait(false);
            PurgeQuarantine();
            await PurgeProcessedStoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // La maintenance ne doit jamais arreter le service : une passe
            // ratee sera rejouee au prochain intervalle.
            logger.LogError(ex, "Passe de maintenance en erreur");
        }
    }

    /// <summary>
    /// Purge les sauvegardes de configuration.
    /// </summary>
    /// <remarks>
    /// Deux regles distinctes, pour deux risques distincts.
    ///
    /// Les sauvegardes CHIFFREES sont le seul moyen de revenir en arriere apres
    /// une modification malheureuse : on en garde un nombre fixe, independamment
    /// de leur age. Dix modifications en une journee ne doivent pas faire perdre
    /// l'etat d'avant.
    ///
    /// Les sauvegardes EN CLAIR portent les secrets non chiffres. Elles n'ont de
    /// raison d'etre que le temps de verifier que le chiffrement a fonctionne :
    /// passe un jour, les conserver est un risque sans contrepartie.
    /// </remarks>
    private void PurgeConfigurationBackups()
    {
        var directory = Path.GetDirectoryName(ConfigurationWriter.SettingsPath);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            // 1. Sauvegardes en clair : supprimees passe vingt-quatre heures.
            var clearCutoff = DateTime.UtcNow.AddDays(-1);

            foreach (var file in Directory.EnumerateFiles(directory, "*.clear.*.bak"))
            {
                if (File.GetLastWriteTimeUtc(file) >= clearCutoff)
                {
                    continue;
                }

                File.Delete(file);

                logger.LogWarning(
                    "Sauvegarde en clair supprimee : {File}. Elle contenait les secrets non chiffres.",
                    Path.GetFileName(file));
            }

            // 2. Sauvegardes chiffrees : les dix plus recentes sont conservees.
            var backups = Directory
                .EnumerateFiles(directory, "appsettings.json.*.bak")
                .Where(f => !f.Contains(".clear.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(10)
                .ToArray();

            foreach (var file in backups)
            {
                File.Delete(file);
                logger.LogDebug("Sauvegarde ancienne supprimee : {File}", Path.GetFileName(file));
            }

            if (backups.Length > 0)
            {
                logger.LogInformation(
                    "{Count} sauvegarde(s) de configuration purgee(s).", backups.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La purge est un confort, pas une fonction du service : son echec
            // ne doit pas interrompre la maintenance ni le traitement.
            logger.LogWarning(ex, "Purge des sauvegardes de configuration impossible.");
        }
    }

    private void CheckDiskSpace()
    {
        var root = pollerOptions.Value.WorkDirectory;

        if (!Directory.Exists(root))
        {
            return;
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);

        if (!drive.IsReady)
        {
            return;
        }

        var freePercent = (int)(drive.AvailableFreeSpace * 100 / drive.TotalSize);

        if (freePercent < maintenanceOptions.Value.MinFreeDiskPercent)
        {
            // Niveau Error et non Warning : c'est une alerte a traiter, pas une
            // information. Le service continue, mais il a une echeance.
            logger.LogError(
                "Espace disque faible sur {Drive} : {Percent} % libres ({Free:N0} octets). "
                + "La capture s'arretera a saturation.",
                drive.Name, freePercent, drive.AvailableFreeSpace);
        }
        else
        {
            logger.LogDebug("Espace disque sur {Drive} : {Percent} % libres", drive.Name, freePercent);
        }
    }

    private async Task QuarantineStaleWorkAsync(CancellationToken cancellationToken)
    {
        var root = pollerOptions.Value.WorkDirectory;

        if (!Directory.Exists(root))
        {
            return;
        }

        var options = maintenanceOptions.Value;
        var now = DateTime.UtcNow;
        var minimumAge = now.AddHours(-options.MinimumAgeHours);
        var quarantineAge = now.AddDays(-options.QuarantineAfterDays);

        var quarantined = 0;
        var deleted = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);

            // Le journal des traitements et la quarantaine ne sont pas des
            // unites de travail.
            if (name.Equals(StateFolderName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(QuarantineFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastWrite = GetLastWriteUtc(directory);

            // Garde-fou principal : un repertoire recemment ecrit est
            // potentiellement en cours de traitement. On n'y touche pas.
            if (lastWrite > minimumAge)
            {
                continue;
            }

            var hasManifest = File.Exists(Path.Combine(directory, "manifest.json"));

            if (!hasManifest)
            {
                // Aucun manifest et plus aucune activite : le message a ete
                // exporte et le nettoyage a echoue, ou le processus est mort
                // avant la premiere sauvegarde. Rien a reprendre.
                if (TryDelete(directory))
                {
                    deleted++;
                }

                continue;
            }

            if (lastWrite > quarantineAge)
            {
                // Unite inachevee mais encore dans la fenetre de reprise.
                continue;
            }

            if (TryQuarantine(root, directory))
            {
                quarantined++;
            }
        }

        if (deleted > 0 || quarantined > 0)
        {
            logger.LogInformation(
                "Maintenance : {Deleted} repertoire(s) orphelin(s) supprime(s), "
                + "{Quarantined} unite(s) inachevee(s) mise(s) en quarantaine",
                deleted, quarantined);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void PurgeQuarantine()
    {
        var quarantine = Path.Combine(pollerOptions.Value.WorkDirectory, QuarantineFolderName);

        if (!Directory.Exists(quarantine))
        {
            return;
        }

        var threshold = DateTime.UtcNow.AddDays(-maintenanceOptions.Value.QuarantineRetentionDays);
        var purged = 0;

        foreach (var directory in Directory.EnumerateDirectories(quarantine))
        {
            if (GetLastWriteUtc(directory) < threshold && TryDelete(directory))
            {
                purged++;
            }
        }

        if (purged > 0)
        {
            // Suppression definitive : elle merite une trace explicite, c'est
            // le dernier moment ou ces donnees existaient.
            logger.LogWarning(
                "Maintenance : {Count} quarantaine(s) supprimee(s) definitivement apres {Days} jours",
                purged, maintenanceOptions.Value.QuarantineRetentionDays);
        }
    }

    private async Task PurgeProcessedStoreAsync(CancellationToken cancellationToken)
    {
        var retention = pollerOptions.Value.ProcessedRetentionDays;
        var threshold = DateTimeOffset.UtcNow.AddDays(-retention);

        foreach (var mailbox in pollerOptions.Value.Configurations
            .Where(c => c.Enabled)
            .Select(c => c.Source.Mailbox)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // La retention doit rester tres superieure au delai pendant lequel
            // un message peut revenir dans la boite. Trop courte, elle provoque
            // des retraitements ; c'est pourquoi le defaut est a 90 jours.
            var count = await processedStore
                .PurgeOlderThanAsync(mailbox, threshold, cancellationToken)
                .ConfigureAwait(false);

            if (count > 0)
            {
                logger.LogInformation(
                    "Maintenance : {Count} entree(s) purgee(s) pour {Mailbox}", count, mailbox);
            }
        }
    }

    /// <summary>
    /// Date de derniere ecriture de l'arborescence, et non du seul repertoire :
    /// sous Windows, la date d'un repertoire ne change pas quand un fichier
    /// d'un sous-repertoire est modifie.
    /// </summary>
    private static DateTime GetLastWriteUtc(string directory)
    {
        var latest = Directory.GetLastWriteTimeUtc(directory);

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var written = File.GetLastWriteTimeUtc(file);

                if (written > latest)
                {
                    latest = written;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Arborescence en cours de modification : on considere le
            // repertoire comme actif, donc intouchable.
            return DateTime.UtcNow;
        }

        return latest;
    }

    private bool TryQuarantine(string root, string directory)
    {
        try
        {
            var quarantine = Path.Combine(
                root,
                QuarantineFolderName,
                DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(quarantine);

            var destination = Path.Combine(quarantine, Path.GetFileName(directory));

            if (Directory.Exists(destination))
            {
                destination += "_" + Guid.NewGuid().ToString("N")[..6];
            }

            Directory.Move(directory, destination);

            logger.LogWarning(
                "Unite de travail abandonnee apres {Days} jours, mise en quarantaine : {Directory}",
                maintenanceOptions.Value.QuarantineAfterDays, destination);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Mise en quarantaine impossible : {Directory}", directory);
            return false;
        }
    }

    private bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Suppression impossible : {Directory}", directory);
            return false;
        }
    }
}
