using System.Diagnostics;
using System.Globalization;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Infrastructure;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ACPoller.Export.Ftp;

/// <summary>Reglages d'une cible FTP ou FTPS.</summary>
public sealed record FtpTargetOptions
{
    /// <summary>Nom d'instance, repris dans les journaux.</summary>
    public string Name { get; init; } = "ftp";

    /// <summary>Hote.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Port. 21 en explicite, 990 en implicite.</summary>
    public int Port { get; init; } = 21;

    /// <summary>Utilisateur.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Mot de passe, dechiffre par le resolveur de configuration.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Repertoire distant de depot.</summary>
    public string RemoteDirectory { get; init; } = "/";

    /// <summary>Chiffrement : "explicit" (recommande), "implicit" ou "none".</summary>
    public string EncryptionMode { get; init; } = "explicit";

    /// <summary>
    /// Accepter tout certificat serveur, y compris auto-signe ou expire.
    /// DESACTIVE PAR DEFAUT, et a n'activer qu'en connaissance de cause :
    /// cela supprime toute protection contre l'interception. La v1 avait cette
    /// validation neutralisee sans que personne ne sache pourquoi.
    /// </summary>
    public bool AcceptAnyCertificate { get; init; }

    /// <summary>Extension du fichier temporaire pendant le transfert.</summary>
    public string TempExtension { get; init; } = ".tmp";
}

/// <summary>
/// Depose les fichiers sur un serveur FTP ou FTPS.
/// </summary>
/// <remarks>
/// L'atomicite passe par un transfert sous nom temporaire suivi d'un renommage
/// distant. C'est indispensable ici plus qu'ailleurs : un transfert FTP peut
/// s'interrompre en cours, et un scrutateur cote serveur ramasserait un fichier
/// partiel sans aucun moyen de s'en apercevoir.
///
/// Une connexion est ouverte par lot puis fermee. Maintenir une connexion
/// ouverte entre les cycles semble plus efficace, mais les serveurs FTP coupent
/// les sessions inactives et les reprises silencieuses sont une source classique
/// d'echecs intermittents impossibles a reproduire.
/// </remarks>
public sealed class FtpExportTarget(
    FtpTargetOptions options,
    ILogger<FtpExportTarget> logger) : IExportTarget
{
    /// <inheritdoc />
    public string Name => options.Name;

    /// <inheritdoc />
    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var client = CreateClient();
            await client.Connect(cancellationToken).ConfigureAwait(false);

            var exists = await client.DirectoryExists(options.RemoteDirectory, cancellationToken)
                .ConfigureAwait(false);

            var details = new Dictionary<string, string>
            {
                ["Chiffrement"] = options.EncryptionMode,
                ["RepertoireDistant"] = exists ? "present" : "absent, sera cree",
            };

            return new ConnectionCheck(
                true,
                $"Connexion etablie sur {options.Host}:{options.Port}",
                stopwatch.Elapsed,
                details);
        }
        catch (Exception ex) when (ex is FtpException or IOException or TimeoutException)
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
            await using var client = CreateClient();
            await client.Connect(cancellationToken).ConfigureAwait(false);

            foreach (var item in batch.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remotePath = CombineRemote(options.RemoteDirectory, item.RelativeName);
                var uploadPath = batch.Policy.AtomicWrite ? remotePath + options.TempExtension : remotePath;

                var status = await client.UploadFile(
                    item.LocalPath,
                    uploadPath,
                    FtpRemoteExists.Overwrite,
                    createRemoteDir: true,
                    token: cancellationToken).ConfigureAwait(false);

                if (status == FtpStatus.Failed)
                {
                    throw new AcPollerException(
                        FailureKind.Transient,
                        $"Transfert echoue : {item.RelativeName}");
                }

                if (batch.Policy.AtomicWrite)
                {
                    // Renommage distant : le scrutateur du serveur ne voit le
                    // fichier sous son nom definitif qu'une fois complet.
                    await client.Rename(uploadPath, remotePath, cancellationToken).ConfigureAwait(false);
                }

                written.Add(remotePath);
            }

            if (!string.IsNullOrWhiteSpace(batch.Policy.SentinelFileName))
            {
                var sentinelName = batch.Policy.SentinelFileName
                    .Replace("{name}", batch.CorrelationId, StringComparison.Ordinal);

                var sentinelPath = CombineRemote(options.RemoteDirectory, sentinelName);

                await client.UploadBytes(
                    System.Text.Encoding.UTF8.GetBytes(batch.CorrelationId),
                    sentinelPath,
                    FtpRemoteExists.Overwrite,
                    createRemoteDir: true,
                    token: cancellationToken).ConfigureAwait(false);

                written.Add(sentinelPath);
            }

            logger.LogDebug(
                "{Target} : {Count} fichier(s) transferes pour {CorrelationId}",
                Name, written.Count, batch.CorrelationId);

            return ExportResult.Ok(written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AcPollerException ex)
        {
            return ExportResult.Ko(ex.Failure);
        }
        catch (FtpAuthenticationException ex)
        {
            // Identifiants refuses : rejouer ne changera rien.
            return ExportResult.Ko(Failure.Permanent($"Authentification refusee : {ex.Message}", exception: ex));
        }
        catch (Exception ex) when (ex is FtpException or IOException or TimeoutException)
        {
            // Coupure, serveur indisponible, delai depasse : rejouable.
            return ExportResult.Ko(Failure.Transient($"Transfert FTP echoue : {ex.Message}", exception: ex));
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(
        ExportBatch batch,
        IReadOnlyList<string> writtenReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writtenReferences);

        try
        {
            await using var client = CreateClient();
            await client.Connect(cancellationToken).ConfigureAwait(false);

            // Ordre inverse : le temoin d'abord, pour invalider le lot au plus tot.
            foreach (var reference in writtenReferences.Reverse())
            {
                try
                {
                    if (await client.FileExists(reference, cancellationToken).ConfigureAwait(false))
                    {
                        await client.DeleteFile(reference, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (FtpException ex)
                {
                    logger.LogError(ex, "Rollback incomplet, fichier distant non supprime : {Path}", reference);
                }
            }
        }
        catch (Exception ex) when (ex is FtpException or IOException or TimeoutException)
        {
            // Le serveur est injoignable pour le nettoyage : depot orphelin,
            // intervention manuelle necessaire.
            logger.LogCritical(
                ex,
                "ROLLBACK FTP IMPOSSIBLE sur {Target} pour {CorrelationId}. Fichiers a nettoyer : {Files}",
                Name, batch.CorrelationId, string.Join(", ", writtenReferences));
        }
    }

    private AsyncFtpClient CreateClient()
    {
        var client = new AsyncFtpClient(options.Host, options.UserName, options.Password, options.Port);

        client.Config.EncryptionMode = options.EncryptionMode.ToLowerInvariant() switch
        {
            "implicit" => FtpEncryptionMode.Implicit,
            "none" => FtpEncryptionMode.None,
            _ => FtpEncryptionMode.Explicit,
        };

        client.Config.ValidateAnyCertificate = options.AcceptAnyCertificate;
        client.Config.RetryAttempts = 1;

        if (options.AcceptAnyCertificate)
        {
            // Trace a chaque session, volontairement : cette option doit rester
            // visible en exploitation et non se faire oublier dans un fichier
            // de configuration pendant des annees.
            logger.LogWarning(
                "{Target} : validation du certificat serveur DESACTIVEE. Connexion vulnerable a l'interception.",
                options.Name);
        }

        return client;
    }

    private static string CombineRemote(string directory, string relativeName)
    {
        // Chemins FTP toujours en slash, quelle que soit la plateforme du serveur.
        var normalized = relativeName.Replace('\\', '/');

        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new AcPollerException(
                FailureKind.Permanent,
                $"Nom de fichier distant invalide : '{relativeName}'.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{directory.TrimEnd('/')}/{normalized.TrimStart('/')}");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fabrique de cibles FTP et FTPS.</summary>
public sealed class FtpExportTargetFactory : IExportTargetFactory
{
    /// <inheritdoc />
    public string TargetType => "ftp";

    /// <inheritdoc />
    public IExportTarget Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var protector = services.GetRequiredService<ISecretProtector>();

        var options = new FtpTargetOptions
        {
            Name = configuration["Name"] ?? "ftp",
            Host = Required(configuration, "Host"),
            Port = int.TryParse(configuration["Port"], out var port) ? port : 21,
            UserName = Required(configuration, "UserName"),

            // Le mot de passe peut arriver chiffre : la resolution de
            // configuration ne dechiffre que les champs connus, pas les
            // reglages libres d'une cible.
            Password = protector.Unprotect(configuration["Password"] ?? string.Empty),

            RemoteDirectory = configuration["RemoteDirectory"] ?? "/",
            EncryptionMode = configuration["EncryptionMode"] ?? "explicit",
            AcceptAnyCertificate = bool.TryParse(configuration["AcceptAnyCertificate"], out var any) && any,
            TempExtension = configuration["TempExtension"] ?? ".tmp",
        };

        return new FtpExportTarget(options, services.GetRequiredService<ILogger<FtpExportTarget>>());
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new AcPollerException(
            FailureKind.Permanent, $"Cible 'ftp' : le reglage '{key}' est obligatoire.");
}
