using System.Diagnostics;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Export;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ACPoller.Export.FileSystem;

/// <summary>Reglages d'une cible systeme de fichiers.</summary>
public sealed record FileSystemTargetOptions
{
    /// <summary>Repertoire de depot. Chemin local ou UNC.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Nom d'instance, repris dans les journaux.</summary>
    public string Name { get; init; } = "fs";

    /// <summary>Creer le repertoire s'il n'existe pas.</summary>
    public bool CreateDirectory { get; init; } = true;

    /// <summary>Extension du fichier temporaire pendant l'ecriture.</summary>
    public string TempExtension { get; init; } = ".tmp";
}

/// <summary>
/// Depose les fichiers dans un repertoire, local ou reseau.
/// </summary>
/// <remarks>
/// C'est la cible la plus utilisee : la majorite des GED scrutent un repertoire.
/// Deux points la rendent sure, et tous deux ont ete appris a leurs depens par
/// des integrations reelles :
///
/// 1. ECRITURE EN DEUX TEMPS. Le fichier est ecrit sous une extension
///    temporaire que le scrutateur ignore, puis renomme. Le renommage est
///    atomique sur un meme volume, donc la GED voit le fichier soit absent,
///    soit complet, jamais a moitie ecrit.
///
/// 2. FICHIER TEMOIN EN DERNIER. Quand la GED l'attend, il n'est ecrit qu'une
///    fois tous les autres fichiers en place. Sans lui, une GED peut prendre
///    le PDF sans son fichier d'information, ou l'inverse.
/// </remarks>
public sealed class FileSystemExportTarget(
    FileSystemTargetOptions options,
    ILogger<FileSystemExportTarget> logger) : IExportTarget
{
    /// <inheritdoc />
    public string Name => options.Name;

    /// <inheritdoc />
    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!Directory.Exists(options.Path))
            {
                if (!options.CreateDirectory)
                {
                    return ConnectionCheck.Ko($"Repertoire inexistant : {options.Path}", stopwatch.Elapsed);
                }

                Directory.CreateDirectory(options.Path);
            }

            // Test d'ECRITURE et non de simple existence : un partage visible
            // mais en lecture seule est le cas le plus frequent, et il ne se
            // verrait autrement qu'au premier depot reel.
            var probe = Path.Combine(options.Path, $".acpoller-probe-{Guid.NewGuid():N}");

            await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);

            return ConnectionCheck.Ok($"Ecriture possible dans {options.Path}", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConnectionCheck.Ko($"{ex.GetType().Name} : {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var written = new List<string>();

        try
        {
            if (options.CreateDirectory)
            {
                Directory.CreateDirectory(options.Path);
            }

            foreach (var item in batch.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = ResolveDestination(item.RelativeName);
                await CopyAsync(item.LocalPath, destination, batch.Policy, cancellationToken)
                    .ConfigureAwait(false);

                written.Add(destination);
            }

            // Temoin ecrit en DERNIER, une fois tous les fichiers en place.
            if (!string.IsNullOrWhiteSpace(batch.Policy.SentinelFileName))
            {
                var sentinel = ResolveDestination(
                    batch.Policy.SentinelFileName.Replace("{name}", batch.CorrelationId, StringComparison.Ordinal));

                await File.WriteAllTextAsync(sentinel, batch.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);

                written.Add(sentinel);
            }

            logger.LogDebug(
                "{Target} : {Count} fichier(s) deposes pour {CorrelationId}",
                Name, written.Count, batch.CorrelationId);

            return ExportResult.Ok(written);
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(written).ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Droits insuffisants : reessayer ne changera rien tant que la
            // configuration NTFS n'a pas ete corrigee.
            await CleanupAsync(written).ConfigureAwait(false);
            return ExportResult.Ko(Failure.Permanent($"Acces refuse : {ex.Message}", exception: ex));
        }
        catch (IOException ex)
        {
            // Partage indisponible, disque plein, verrou : potentiellement
            // transitoire, le rejeu a du sens.
            await CleanupAsync(written).ConfigureAwait(false);
            return ExportResult.Ko(Failure.Transient($"Ecriture impossible : {ex.Message}", exception: ex));
        }
    }

    /// <inheritdoc />
    public Task RollbackAsync(
        ExportBatch batch,
        IReadOnlyList<string> writtenReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writtenReferences);

        // Ordre inverse : le temoin part en premier, pour que la GED cesse
        // immediatement de considerer le lot comme complet pendant le nettoyage.
        foreach (var reference in writtenReferences.Reverse())
        {
            try
            {
                if (File.Exists(reference))
                {
                    File.Delete(reference);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un rollback partiel se signale fort : il faut une intervention.
                logger.LogError(ex, "Rollback incomplet, fichier non supprime : {Path}", reference);
            }
        }

        return Task.CompletedTask;
    }

    private async Task CopyAsync(
        string source,
        string destination,
        ExportPolicy policy,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (!policy.AtomicWrite)
        {
            File.Copy(source, destination, overwrite: true);
            return;
        }

        var temporary = destination + options.TempExtension;

        // Copie par flux plutot que File.Copy : sur un partage reseau, la copie
        // d'un fichier volumineux devient interruptible.
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(temporary))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        // Renommage atomique sur un meme volume : c'est ce qui garantit que la
        // GED ne voit jamais un fichier partiel.
        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>
    /// Resout le chemin de destination en interdisant toute sortie du
    /// repertoire cible. Le nom vient d'un gabarit resolu a partir de donnees
    /// du message, donc potentiellement d'un sujet de mail : il ne doit jamais
    /// pouvoir contenir un ".." exploitable.
    /// </summary>
    private string ResolveDestination(string relativeName)
    {
        var root = Path.GetFullPath(options.Path) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(options.Path, relativeName));

        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new AcPollerException(
                FailureKind.Permanent,
                $"Nom de fichier sortant du repertoire cible : '{relativeName}'.");
        }

        return destination;
    }

    private async Task CleanupAsync(List<string> written)
    {
        foreach (var path in written)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Nettoyage partiel impossible : {Path}", path);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fabrique de cibles systeme de fichiers.</summary>
public sealed class FileSystemExportTargetFactory : IExportTargetFactory
{
    /// <inheritdoc />
    public string TargetType => "fs";

    /// <inheritdoc />
    public IExportTarget Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FileSystemTargetOptions
        {
            Path = configuration["Path"] ?? throw new AcPollerException(
                FailureKind.Permanent, "Cible 'fs' : le reglage 'Path' est obligatoire."),
            Name = configuration["Name"] ?? "fs",
            CreateDirectory = !bool.TryParse(configuration["CreateDirectory"], out var create) || create,
            TempExtension = configuration["TempExtension"] ?? ".tmp",
        };

        return new FileSystemExportTarget(
            options,
            services.GetRequiredService<ILogger<FileSystemExportTarget>>());
    }
}
