using ACPoller.Abstractions.Metadata;
using ACPoller.Core.Configuration;
using ACPoller.Core.Metadata;
using ACPoller.Core.Plugins;
using ACPoller.Core.Workers;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ACPoller.Service.Control;

/// <summary>Reglages de l'API de pilotage.</summary>
public sealed record ControlApiOptions
{
    /// <summary>Port d'ecoute sur la boucle locale.</summary>
    public int Port { get; init; } = 5199;

    /// <summary>
    /// Jeton partage exige dans l'en-tete X-ACPoller-Token. Vide pour desactiver
    /// le controle, ce qui n'est acceptable qu'en developpement : tout processus
    /// local pourrait sinon relancer des workers ou lire la configuration.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>Activer l'API. Desactivee, le service tourne sans interface de pilotage.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Expose l'etat et les commandes du service a l'UI de parametrage.
/// </summary>
/// <remarks>
/// Ecoute UNIQUEMENT sur 127.0.0.1 : l'UI tourne sur la meme machine que le
/// service, et rien ne justifie d'exposer ces commandes sur le reseau. C'est
/// aussi ce qui rend le jeton partage suffisant, sans authentification forte.
///
/// Le principe qui gouverne ce fichier : l'UI ne modifie JAMAIS directement les
/// fichiers de configuration. Elle passe par ici, ou le service reste seul
/// maitre de ce qu'il lit et de quand il le relit. Une UI qui ecrit un fichier
/// pendant qu'un worker le relit produit un etat incoherent, sans erreur.
/// </remarks>
public static class ControlEndpoints
{
    private const string TokenHeader = "X-ACPoller-Token";

    /// <summary>Declare les points d'entree de pilotage.</summary>
    /// <param name="app">Application web.</param>
    /// <returns>L'application, pour le chainage.</returns>
    public static WebApplication MapControlEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api").AddEndpointFilter(TokenFilter);

        group.MapGet("/status", GetStatus);
        group.MapGet("/configurations", GetConfigurations);
        group.MapPost("/configurations/{name}/test", TestConfiguration);
        group.MapPost("/workers/{name}/restart", RestartWorker);
        group.MapPost("/maintenance/run", RunMaintenance);
        group.MapGet("/processors", GetProcessors);
        group.MapGet("/plugins", GetPlugins);
        group.MapConfigurationWriteEndpoints();
        group.MapServiceSettingsEndpoints();
        group.MapTemplateEndpoints();
        group.MapDashboardEndpoints();
        return app;
    }

    /// <summary>
    /// Liste les traitements metier disponibles, tels que les plugins charges
    /// les declarent.
    /// </summary>
    /// <remarks>
    /// Les noms viennent des instances REELLEMENT chargees et non d'une
    /// inspection de metadonnees : c'est la propriete Name du processeur qui
    /// fait foi en configuration, et rien ne garantit qu'elle corresponde au
    /// nom du type.
    ///
    /// Les plugins refuses sont renvoyes a part, avec leur motif : sans cela,
    /// un exploitant cherche un processeur qui existe bien dans le repertoire
    /// mais dont le plugin n'a pas ete charge.
    /// </remarks>
    private static IResult GetProcessors(PluginManager plugins)
    {
        var host = plugins.Current;

        return Results.Ok(new
        {
            processors = host.Processors.Select(p => new
            {
                name = p.Name,
                stages = p.Stages.Select(s => s.ToString()).ToArray(),
                order = p.Order,
            }),

            rejectedPlugins = host.Descriptors
                .Where(d => !d.IsCompatible)
                .Select(d => new
                {
                    name = d.Name,
                    path = d.AssemblyPath,
                    reason = d.IncompatibilityReason,
                }),
        });
    }

    private static async ValueTask<object?> TokenFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<ControlApiOptions>();

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            return await next(context).ConfigureAwait(false);
        }

        var provided = context.HttpContext.Request.Headers[TokenHeader].ToString();

        // Comparaison a temps constant : le jeton est court et local, mais
        // rien ne justifie de laisser une fuite par mesure de temps.
        if (!CryptographicEquals(provided, options.Token))
        {
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("ACPoller.Control")
                .LogWarning(
                    "Jeton refuse pour {Path} ({Length} caracteres recus)",
                    context.HttpContext.Request.Path,
                    provided.Length);

            return Results.Unauthorized();
        }

        return await next(context).ConfigureAwait(false);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left);
        var b = System.Text.Encoding.UTF8.GetBytes(right);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IResult GetStatus(
        PollerHostedService workers,
        RestartTracker restartTracker,
        GlobalDiskInfo disk,
        IOptions<PollerOptions> options)
    {
        var status = new ServiceStatus
        {
            Version = typeof(ControlEndpoints).Assembly.GetName().Version?.ToString() ?? "inconnue",
            StartedUtc = ServiceStartedUtc,
            MaxGlobalConcurrency = options.Value.MaxGlobalConcurrency,
            FreeDiskPercent = disk.GetFreeSpacePercent(options.Value.WorkDirectory),
            RestartRequired = restartTracker.IsRestartRequired,
            PendingRestart =
            [
                .. restartTracker.Pending.Select(p => new PendingRestart
                {
                    Path = p.Path,
                    Reason = p.Reason,
                })
            ],
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
        };

        return Results.Ok(status);
    }

    private static IResult GetConfigurations(
        ConfigurationResolver resolver,
        ConfigurationValidator validator,
        IConfiguration configuration)
    {
        var configurations = resolver.Resolve(configuration);
        var issues = validator.Validate(configurations);

        // Les secrets ne sortent JAMAIS de l'API, meme en local et meme
        // authentifie. L'UI n'a pas besoin de les lire pour les modifier :
        // elle envoie une nouvelle valeur ou ne touche pas au champ.
        var payload = configurations.Select(c => new ConfigurationSummary
        {
            Name = c.Name,
            Enabled = c.Enabled,
            Template = c.Template,
            Protocol = c.Source.Protocol,
            Mailbox = c.Source.Mailbox,
            HasSecret = !string.IsNullOrWhiteSpace(c.Source.ClientSecret)
                || !string.IsNullOrWhiteSpace(c.Source.Password),
            UsesCertificate = !string.IsNullOrWhiteSpace(c.Source.CertificateThumbprint),
            PdfMode = c.Output.PdfMode.ToString(),
            MetadataFormat = c.Output.Metadata.Format,
            Targets = [.. c.Output.Targets.Select(t => $"{t.Type}:{t.Name}")],
            Processors = [.. c.Processors],
            IntervalSeconds = c.Schedule.IntervalSeconds,
            Issues =
            [
                .. issues.Where(i => i.ConfigurationName == c.Name)
                    .Select(i => new IssueSummary
                    {
                        Path = i.Path,
                        Message = i.Message,
                        Blocking = i.IsBlocking,
                    })
            ],
        });

        return Results.Ok(payload);
    }

    private static async Task<IResult> TestConfiguration(
        string name,
        ConfigurationResolver resolver,
        RuntimeFactory runtimeFactory,
        IEnumerable<IMetadataWriter> metadataWriters,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var config = resolver.Resolve(configuration)
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            return Results.NotFound(new { message = $"Configuration '{name}' introuvable." });
        }

        var results = new List<ComponentCheck>();

        // La feuille de style est verifiee EN PREMIER, et sans reseau : une
        // feuille fautive est le seul defaut de cette liste qui produirait des
        // fichiers de sortie inexploitables tout en laissant la capture
        // fonctionner. Elle se decouvrirait autrement sur la premiere facture.
        var stylesheetCheck = ValidateStylesheet(config, metadataWriters);

        if (stylesheetCheck is not null)
        {
            results.Add(stylesheetCheck);
        }

        // Source d'abord : sans elle, tester les cibles n'apprend rien d'utile.
        await using (var source = runtimeFactory.CreateSource(config))
        {
            var check = await source.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

            results.Add(new ComponentCheck
            {
                Component = $"source:{config.Source.Protocol}",
                Success = check.Success,
                Message = check.Message,
                ElapsedMs = (int)check.Elapsed.TotalMilliseconds,
                Details = check.Details,
            });
        }

        var components = runtimeFactory.CreateComponents(config);

        try
        {
            foreach (var (target, _) in components.Targets)
            {
                var check = await target.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

                results.Add(new ComponentCheck
                {
                    Component = $"cible:{target.Name}",
                    Success = check.Success,
                    Message = check.Message,
                    ElapsedMs = (int)check.Elapsed.TotalMilliseconds,
                    Details = check.Details,
                });
            }
        }
        finally
        {
            foreach (var (target, _) in components.Targets)
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }

        return Results.Ok(results);
    }

    /// <summary>
    /// Verifie la feuille de transformation quand le format l'exige.
    /// Retourne null si le format ne repose pas sur une feuille.
    /// </summary>
    private static ComponentCheck? ValidateStylesheet(
        PollerConfiguration configuration,
        IEnumerable<IMetadataWriter> metadataWriters)
    {
        if (!string.Equals(configuration.Output.Metadata.Format, "xslt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        var path = configuration.Output.Metadata.Template;

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ComponentCheck
            {
                Component = "feuille de style",
                Success = false,
                Message = "Format 'xslt' sans feuille declaree dans Output:Metadata:Template.",
                ElapsedMs = 0,
            };
        }

        var writer = metadataWriters.OfType<XsltMetadataWriter>().FirstOrDefault();

        if (writer is null)
        {
            return new ComponentCheck
            {
                Component = "feuille de style",
                Success = false,
                Message = "Writer 'xslt' non enregistre dans le service.",
                ElapsedMs = 0,
            };
        }

        // La compilation est le vrai test : une feuille syntaxiquement valide
        // mais utilisant une fonction indisponible echoue ici, pas au parsing.
        var error = writer.Validate(path);

        return new ComponentCheck
        {
            Component = "feuille de style",
            Success = error is null,
            Message = error ?? $"Feuille compilee : {path}",
            ElapsedMs = (int)stopwatch.Elapsed.TotalMilliseconds,
        };
    }


    private static async Task<IResult> RestartWorker(
        string name,
        PollerHostedService workers,
        CancellationToken cancellationToken)
    {
        // Redemarre UNE boite sans toucher aux autres : c'est tout l'interet du
        // decouplage des jetons d'annulation cote worker.
        await workers.RestartWorkerAsync(name, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new { message = $"Worker '{name}' redemarre." });
    }

    private static async Task<IResult> RunMaintenance(
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        await maintenance.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { message = "Passe de maintenance effectuee." });
    }

    private static IResult GetPlugins(PluginManager plugins)
    {
        // Les plugins incompatibles sont exposes AVEC leur motif : c'est
        // exactement l'information qui manquait en v1, quand un plugin mal
        // compile ne se signalait qu'a la premiere facture.
        return Results.Ok(plugins.Current.Descriptors);
    }

    /// <summary>Horodatage de demarrage, renseigne par le programme principal.</summary>
    public static DateTimeOffset ServiceStartedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Fournit l'espace disque disponible, isole pour rester testable.</summary>
public sealed class GlobalDiskInfo
{
    /// <summary>Pourcentage d'espace libre sur le volume du chemin donne.</summary>
    /// <param name="path">Chemin de reference.</param>
    /// <returns>Le pourcentage libre, ou null si indeterminable.</returns>
    public int? GetFreeSpacePercent(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.IsReady ? (int)(drive.AvailableFreeSpace * 100 / drive.TotalSize) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Etat global du service.</summary>
public sealed record ServiceStatus
{
    /// <summary>Version de l'assemblage.</summary>
    public required string Version { get; init; }

    /// <summary>Demarrage du service.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Concurrence globale configuree.</summary>
    public int MaxGlobalConcurrency { get; init; }

    /// <summary>Espace libre sur le volume de travail, en pourcentage.</summary>
    public int? FreeDiskPercent { get; init; }

    /// <summary>Etat de chaque worker.</summary>
    public IReadOnlyList<WorkerStatus> Workers { get; init; } = [];

    /// <summary>Vrai si un reglage modifie attend un redemarrage du service.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Reglages modifies en attente, avec la raison.</summary>
    public IReadOnlyList<PendingRestart> PendingRestart { get; init; } = [];
}

/// <summary>Etat d'un worker.</summary>
public sealed record WorkerStatus
{
    /// <summary>Nom de la configuration servie.</summary>
    public required string Name { get; init; }

    /// <summary>Etat courant.</summary>
    public required string State { get; init; }

    /// <summary>Fin du dernier cycle acheve.</summary>
    public DateTimeOffset? LastCycleUtc { get; init; }

    /// <summary>Derniere erreur, affichable dans l'UI.</summary>
    public string? LastError { get; init; }
}

/// <summary>Resume d'une configuration, sans aucun secret.</summary>
public sealed record ConfigurationSummary
{
    /// <summary>Nom.</summary>
    public required string Name { get; init; }

    /// <summary>Active ou non.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gabarit dont elle herite.</summary>
    public string? Template { get; init; }

    /// <summary>Protocole de collecte.</summary>
    public required string Protocol { get; init; }

    /// <summary>Boite surveillee.</summary>
    public required string Mailbox { get; init; }

    /// <summary>Un secret est renseigne. La valeur n'est jamais exposee.</summary>
    public bool HasSecret { get; init; }

    /// <summary>L'authentification passe par un certificat.</summary>
    public bool UsesCertificate { get; init; }

    /// <summary>Mode de production des PDF.</summary>
    public required string PdfMode { get; init; }

    /// <summary>Format du fichier d'information.</summary>
    public required string MetadataFormat { get; init; }

    /// <summary>Cibles d'export declarees.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>Traitements metier actifs.</summary>
    public IReadOnlyList<string> Processors { get; init; } = [];

    /// <summary>Periode de collecte.</summary>
    public int IntervalSeconds { get; init; }

    /// <summary>Anomalies de configuration detectees.</summary>
    public IReadOnlyList<IssueSummary> Issues { get; init; } = [];
}

/// <summary>Anomalie de configuration exposee a l'UI.</summary>
public sealed record IssueSummary
{
    /// <summary>Chemin du reglage fautif.</summary>
    public required string Path { get; init; }

    /// <summary>Message affichable.</summary>
    public required string Message { get; init; }

    /// <summary>Vrai si la configuration est ecartee.</summary>
    public bool Blocking { get; init; }
}

/// <summary>Resultat du test d'un composant.</summary>
public sealed record ComponentCheck
{
    /// <summary>Composant teste.</summary>
    public required string Component { get; init; }

    /// <summary>Succes du test.</summary>
    public bool Success { get; init; }

    /// <summary>Message affichable tel quel.</summary>
    public required string Message { get; init; }

    /// <summary>Duree du test, en millisecondes.</summary>
    public int ElapsedMs { get; init; }

    /// <summary>Informations complementaires.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary>Un reglage modifie qui n'a pas encore pris effet.</summary>
public sealed record PendingRestart
{
    /// <summary>Chemin du reglage.</summary>
    public required string Path { get; init; }

    /// <summary>Pourquoi un redemarrage est necessaire.</summary>
    public required string Reason { get; init; }
}
