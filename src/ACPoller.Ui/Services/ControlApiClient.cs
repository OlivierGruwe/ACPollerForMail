using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ACPoller.Ui.Services;

/// <summary>Reglages de connexion au service, propres au poste de l'exploitant.</summary>
/// <remarks>
/// Stockes dans le profil de l'utilisateur et non dans l'appsettings du service :
/// l'UI et le service sont deux applications distinctes, et le jeton saisi par
/// un exploitant n'a rien a faire dans le fichier que lit le service.
/// </remarks>
public sealed record ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Langue de l'interface.</summary>
    public AppLanguage Language { get; init; } = AppLanguage.Systeme;

    /// <summary>Adresse de l'API, toujours sur la boucle locale.</summary>
    public string BaseAddress { get; init; } = "http://127.0.0.1:5199";

    /// <summary>Jeton partage, tel que declare dans la configuration du service.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>Theme d'affichage retenu par l'exploitant.</summary>
    public AppTheme Theme { get; init; } = AppTheme.Systeme;

    /// <summary>
    /// Emplacement du fichier de reglages. Public car affiche dans l'ecran de
    /// reglages : c'est la premiere question du support quand quelque chose ne
    /// va pas, autant y repondre avant qu'elle ne soit posee.
    /// </summary>
    /// <summary>
    /// Reglages propres a l'utilisateur. Ecrits des qu'il modifie quelque
    /// chose, et prioritaires sur les valeurs deposees a l'installation.
    /// </summary>
    public static string Location => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ACPoller",
        "ui-settings.json");

    /// <summary>
    /// Reglages par defaut deposes par l'installeur, communs a tous les
    /// exploitants du serveur. Ils portent l'adresse et le jeton de pilotage.
    /// </summary>
    public static string DefaultsLocation => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ACPoller",
        "ui-settings.default.json");

    /// <summary>
    /// Charge les reglages, en trois temps.
    /// </summary>
    /// <remarks>
    /// Ordre : fichier de l'utilisateur, puis valeurs deposees a
    /// l'installation, puis valeurs codees. C'est ce qui fait qu'un exploitant
    /// qui ouvre l'interface pour la premiere fois sur un serveur fraichement
    /// installe est connecte sans rien saisir, tout en gardant la possibilite
    /// de pointer sur un autre service.
    /// </remarks>
    /// <returns>Les reglages applicables.</returns>
    public static ClientSettings Load()
    {
        var user = TryRead(Location, warnOnError: true);

        if (user is not null)
        {
            return user;
        }

        // Les defauts machine ne declenchent PAS d'avertissement s'ils sont
        // illisibles : leur absence est normale sur un poste ou seule
        // l'interface est installee.
        return TryRead(DefaultsLocation, warnOnError: false) ?? new ClientSettings();
    }

    private static ClientSettings? TryRead(string path, bool warnOnError)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            if (warnOnError)
            {
                // Un fichier illisible doit se dire : retourner les valeurs par
                // defaut en silence envoie l'exploitant chercher une panne de
                // service alors qu'il y a une virgule en trop.
                System.Windows.MessageBox.Show(
                    $"Fichier de reglages illisible :\n{path}\n\n{ex.Message}",
                    "ACPoller",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }

            return null;
        }
    }

    /// <summary>Enregistre les reglages dans le profil utilisateur.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
        File.WriteAllText(Location, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>
/// Dialogue avec l'API de pilotage du service.
/// </summary>
/// <remarks>
/// Les types de reponse sont redeclares ici plutot que partages avec le service.
/// C'est delibere : l'UI consomme un CONTRAT HTTP, pas des classes .NET. Un
/// partage d'assemblage rendrait toute evolution du service bloquante pour
/// l'UI, alors que les deux se deploient separement.
/// </remarks>
public sealed class ControlApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Construit le client a partir des reglages du poste.</summary>
    /// <param name="settings">Reglages de connexion.</param>
    public ControlApiClient(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseAddress),

            // Un test de connexion FTP ou Graph peut prendre du temps : le
            // delai par defaut de 100 secondes est trop court pour un ecran
            // qui teste plusieurs cibles a la suite.
            Timeout = TimeSpan.FromMinutes(3),
        };

        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            _http.DefaultRequestHeaders.Add("X-ACPoller-Token", settings.Token);
        }
    }

    /// <summary>Recupere les reglages de service, secrets masques.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les reglages et l'empreinte du fichier.</returns>
    public Task<ServiceSettingsDto?> GetServiceSettingsAsync(CancellationToken cancellationToken) =>
        GetAsync<ServiceSettingsDto>("api/settings", cancellationToken);

    /// <summary>Enregistre les reglages de service.</summary>
    /// <param name="json">Reglages edites.</param>
    /// <param name="version">Empreinte du fichier.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public async Task<WriteResultDto> UpdateServiceSettingsAsync(
        JsonNode json,
        string? version,
        CancellationToken cancellationToken)
    {
        var body = new { configuration = json, version };

        using var response = await _http
            .PutAsJsonAsync("api/settings", body, cancellationToken).ConfigureAwait(false);

        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Liste les traitements metier disponibles et les plugins refuses.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les processeurs charges, et les plugins ecartes avec leur motif.</returns>
    public async Task<ProcessorListDto> GetProcessorsAsync(CancellationToken cancellationToken) =>
        await GetAsync<ProcessorListDto>("api/processors", cancellationToken).ConfigureAwait(false)
        ?? new ProcessorListDto();

    /// <summary>Recupere l'etat global du service.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'etat du service.</returns>
    public Task<ServiceStatusDto?> GetStatusAsync(CancellationToken cancellationToken) =>
        GetAsync<ServiceStatusDto>("api/status", cancellationToken);

    /// <summary>Recupere les configurations et leurs anomalies.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les configurations declarees.</returns>
    public async Task<IReadOnlyList<ConfigurationDto>> GetConfigurationsAsync(CancellationToken cancellationToken) =>
        await GetAsync<List<ConfigurationDto>>("api/configurations", cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Teste la source et les cibles d'une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le resultat par composant.</returns>
    public async Task<IReadOnlyList<ComponentCheckDto>> TestAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync($"api/configurations/{Uri.EscapeDataString(name)}/test", null, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<List<ComponentCheckDto>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    /// <summary>Redemarre un worker sans toucher aux autres.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee au retour du service.</returns>
    public async Task RestartWorkerAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync($"api/workers/{Uri.EscapeDataString(name)}/restart", null, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Declenche une passe de maintenance.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee au retour du service.</returns>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync("api/maintenance/run", null, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Recupere les plugins detectes, compatibles ou non.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les plugins detectes.</returns>
    public async Task<IReadOnlyList<PluginDto>> GetPluginsAsync(CancellationToken cancellationToken) =>
        await GetAsync<List<PluginDto>>("api/plugins", cancellationToken).ConfigureAwait(false) ?? [];


    /// <summary>Recupere le JSON brut d'une configuration, secrets masques.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le JSON et l'empreinte du fichier.</returns>
    public Task<RawConfigurationDto?> GetRawAsync(string name, CancellationToken cancellationToken) =>
        GetAsync<RawConfigurationDto>(
            $"api/configurations/{Uri.EscapeDataString(name)}/raw", cancellationToken);

    /// <summary>Cree ou remplace une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="json">Contenu JSON edite.</param>
    /// <param name="version">Empreinte du fichier lue avant edition.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de l'ecriture, refus compris.</returns>
    public async Task<WriteResultDto> UpsertAsync(
        string name,
        JsonNode json,
        string? version,
        CancellationToken cancellationToken)
    {
        var body = new { configuration = json, version };

        using var response = await _http
            .PutAsJsonAsync($"api/configurations/{Uri.EscapeDataString(name)}", body, cancellationToken)
            .ConfigureAwait(false);

        // 409 n'est pas une erreur de transport mais un refus metier : conflit
        // de version ou configuration invalide. Le corps porte le detail, il
        // faut le lire au lieu de lever.
        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Supprime une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="version">Empreinte du fichier.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de la suppression.</returns>
    public async Task<WriteResultDto> DeleteConfigurationAsync(
        string name,
        string? version,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .DeleteAsync(
                $"api/configurations/{Uri.EscapeDataString(name)}?version={Uri.EscapeDataString(version ?? string.Empty)}",
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Active ou desactive une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="enabled">Nouvel etat.</param>
    /// <param name="version">Empreinte du fichier.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de l'operation.</returns>
    public async Task<WriteResultDto> SetEnabledAsync(
        string name,
        bool enabled,
        string? version,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync(
                $"api/configurations/{Uri.EscapeDataString(name)}/enabled",
                new { enabled, version },
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WriteResultDto> ReadWriteResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content
            .ReadFromJsonAsync<WriteResultDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return result ?? new WriteResultDto
        {
            Success = false,
            Message = $"Reponse inattendue du service ({(int)response.StatusCode}).",
        };
    }


    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>Liste les gabarits declares et leur portee.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les gabarits, vide s'il n'y en a pas.</returns>
    public async Task<IReadOnlyList<TemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var response = await GetAsync<TemplateListDto>("api/templates", cancellationToken)
            .ConfigureAwait(false);

        return response?.Templates ?? [];
    }

    /// <summary>Recupere le JSON brut d'un gabarit, secrets masques.</summary>
    /// <param name="name">Nom du gabarit.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le JSON et l'empreinte du fichier.</returns>
    public Task<RawConfigurationDto?> GetTemplateRawAsync(string name, CancellationToken cancellationToken) =>
        GetAsync<RawConfigurationDto>(
            $"api/templates/{Uri.EscapeDataString(name)}/raw", cancellationToken);

    /// <summary>Cree ou remplace un gabarit.</summary>
    /// <param name="name">Nom du gabarit.</param>
    /// <param name="json">Contenu JSON.</param>
    /// <param name="version">Empreinte du fichier.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public async Task<WriteResultDto> UpsertTemplateAsync(
        string name,
        JsonNode json,
        string? version,
        CancellationToken cancellationToken)
    {
        var body = new { configuration = json, version };

        using var response = await _http
            .PutAsJsonAsync($"api/templates/{Uri.EscapeDataString(name)}", body, cancellationToken)
            .ConfigureAwait(false);

        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Supprime un gabarit.</summary>
    /// <param name="name">Nom du gabarit.</param>
    /// <param name="version">Empreinte du fichier.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>L'issue de la suppression.</returns>
    public async Task<WriteResultDto> DeleteTemplateAsync(
        string name,
        string? version,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(version ?? string.Empty);

        using var response = await _http
            .DeleteAsync($"api/templates/{Uri.EscapeDataString(name)}?version={query}", cancellationToken)
            .ConfigureAwait(false);

        return await ReadWriteResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recupere les donnees du tableau de bord.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Les indicateurs agreges.</returns>
    public Task<DashboardDto?> GetDashboardAsync(CancellationToken cancellationToken) =>
        GetAsync<DashboardDto>("api/dashboard", cancellationToken);

    /// <summary>Remet les compteurs a zero.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee au retour du service.</returns>
    public async Task ResetMetricsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync("api/dashboard/reset", null, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Liste des gabarits, avec l'empreinte du fichier.</summary>
public sealed record TemplateListDto
{
    /// <summary>Empreinte du fichier au moment de la lecture.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gabarits declares.</summary>
    public List<TemplateDto> Templates { get; init; } = [];
}

/// <summary>Un gabarit et sa portee.</summary>
public sealed record TemplateDto
{
    /// <summary>Nom du gabarit.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Configurations qui en heritent.</summary>
    public List<string> UsedBy { get; init; } = [];

    /// <summary>Protocole declare, a titre indicatif.</summary>
    public string? Protocol { get; init; }

    /// <summary>Libelle affichable dans la liste.</summary>
    public string Display => UsedBy.Count == 0
        ? $"{Name} (inutilise)"
        : $"{Name} ({UsedBy.Count} configuration(s))";
}

/// <summary>Etat global du service.</summary>
public sealed record ServiceStatusDto
{
    /// <summary>Version du service.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Demarrage du service.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Concurrence globale configuree.</summary>
    public int MaxGlobalConcurrency { get; init; }

    /// <summary>Espace libre sur le volume de travail.</summary>
    public int? FreeDiskPercent { get; init; }

    /// <summary>Etat de chaque worker.</summary>
    public List<WorkerStatusDto> Workers { get; init; } = [];

    /// <summary>Vrai si un reglage modifie attend un redemarrage.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Reglages en attente, avec la raison.</summary>
    public List<PendingRestartDto> PendingRestart { get; init; } = [];
}

/// <summary>Un reglage modifie qui attend un redemarrage.</summary>
public sealed record PendingRestartDto
{
    /// <summary>Chemin du reglage.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Pourquoi un redemarrage est necessaire.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Etat d'un worker.</summary>
public sealed record WorkerStatusDto
{
    /// <summary>Nom de la configuration.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Etat courant.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Fin du dernier cycle.</summary>
    public DateTimeOffset? LastCycleUtc { get; init; }

    /// <summary>Derniere erreur.</summary>
    public string? LastError { get; init; }
}

/// <summary>Configuration exposee par le service, sans secret.</summary>
public sealed record ConfigurationDto
{
    /// <summary>Nom.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Active ou non.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gabarit dont elle herite.</summary>
    public string? Template { get; init; }

    /// <summary>Protocole de collecte.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Boite surveillee.</summary>
    public string Mailbox { get; init; } = string.Empty;

    /// <summary>Un secret est renseigne. La valeur n'est jamais exposee.</summary>
    public bool HasSecret { get; init; }

    /// <summary>Authentification par certificat.</summary>
    public bool UsesCertificate { get; init; }

    /// <summary>Mode de production des PDF.</summary>
    public string PdfMode { get; init; } = string.Empty;

    /// <summary>Format du fichier d'information.</summary>
    public string MetadataFormat { get; init; } = string.Empty;

    /// <summary>Cibles declarees.</summary>
    public List<string> Targets { get; init; } = [];

    /// <summary>Traitements metier actifs.</summary>
    public List<string> Processors { get; init; } = [];

    /// <summary>Periode de collecte.</summary>
    public int IntervalSeconds { get; init; }

    /// <summary>Anomalies detectees.</summary>
    public List<IssueDto> Issues { get; init; } = [];
}

/// <summary>Anomalie de configuration.</summary>
public sealed record IssueDto
{
    /// <summary>Chemin du reglage fautif.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Message affichable.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Vrai si la configuration est ecartee.</summary>
    public bool Blocking { get; init; }
}

/// <summary>Resultat du test d'un composant.</summary>
public sealed record ComponentCheckDto
{
    /// <summary>Composant teste.</summary>
    public string Component { get; init; } = string.Empty;

    /// <summary>Succes du test.</summary>
    public bool Success { get; init; }

    /// <summary>Message affichable.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Duree du test.</summary>
    public int ElapsedMs { get; init; }
}

/// <summary>Plugin detecte par le service.</summary>
public sealed record PluginDto
{
    /// <summary>Nom.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Chemin de l'assemblage.</summary>
    public string AssemblyPath { get; init; } = string.Empty;

    /// <summary>Version de contrat declaree.</summary>
    public int ContractVersion { get; init; }

    /// <summary>Compatible avec ce socle.</summary>
    public bool IsCompatible { get; init; }

    /// <summary>Motif d'incompatibilite.</summary>
    public string? IncompatibilityReason { get; init; }
}

/// <summary>JSON brut d'une configuration, avec l'empreinte du fichier.</summary>
public sealed record RawConfigurationDto
{
    /// <summary>Empreinte du fichier au moment de la lecture.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Contenu de la configuration, secrets masques par une chaine vide.</summary>
    public JsonNode? Configuration { get; init; }
}

/// <summary>Issue d'une ecriture de configuration.</summary>
public sealed record WriteResultDto
{
    /// <summary>Vrai si le fichier a ete modifie.</summary>
    public bool Success { get; init; }

    /// <summary>Message affichable.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Nouvelle empreinte, a reutiliser pour la prochaine ecriture.</summary>
    public string? Version { get; init; }

    /// <summary>Anomalies de validation, en cas de refus.</summary>
    public List<IssueDto> Issues { get; init; } = [];
}

/// <summary>Indicateurs du tableau de bord.</summary>
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

    /// <summary>95e centile de la duree.</summary>
    public int P95DurationMs { get; init; }

    /// <summary>Limitations Graph rencontrees.</summary>
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

    /// <summary>Ventilation par configuration.</summary>
    public List<ConfigurationMetricsDto> ByConfiguration { get; init; } = [];

    /// <summary>Volume exporte par heure.</summary>
    public List<HourlyPointDto> HourlyExported { get; init; } = [];
}

/// <summary>Metriques d'une configuration.</summary>
public sealed record ConfigurationMetricsDto
{
    /// <summary>Nom de la configuration.</summary>
    public string Name { get; init; } = string.Empty;

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
public sealed record HourlyPointDto
{
    /// <summary>Debut de l'heure, en UTC.</summary>
    public DateTimeOffset HourUtc { get; init; }

    /// <summary>Valeur cumulee sur l'heure.</summary>
    public double Value { get; init; }
}

/// <summary>Traitements metier disponibles et plugins refuses.</summary>
public sealed record ProcessorListDto
{
    /// <summary>Processeurs charges et activables.</summary>
    public List<ProcessorDto> Processors { get; init; } = [];

    /// <summary>Plugins presents mais non charges.</summary>
    public List<RejectedPluginDto> RejectedPlugins { get; init; } = [];
}

/// <summary>Un traitement metier disponible.</summary>
public sealed record ProcessorDto
{
    /// <summary>Nom declare par le processeur. C'est lui qui fait foi en configuration.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Etapes du pipeline auxquelles il intervient.</summary>
    public List<string> Stages { get; init; } = [];

    /// <summary>Ordre d'execution entre processeurs d'une meme etape.</summary>
    public int Order { get; init; }

    /// <summary>Libelle des etapes, affichable.</summary>
    public string StagesLabel => string.Join(", ", Stages);
}

/// <summary>Un plugin present mais non charge.</summary>
public sealed record RejectedPluginDto
{
    /// <summary>Nom du plugin.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Chemin de l'assemblage.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Motif du refus, affichable tel quel.</summary>
    public string? Reason { get; init; }
}

/// <summary>Reglages de service, avec l'empreinte du fichier.</summary>
public sealed record ServiceSettingsDto
{
    /// <summary>Empreinte du fichier au moment de la lecture.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Reglages, secrets masques par une chaine vide.</summary>
    public JsonNode? Settings { get; init; }
}
