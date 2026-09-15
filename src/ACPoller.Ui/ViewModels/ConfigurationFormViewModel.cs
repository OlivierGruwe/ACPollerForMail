using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACPoller.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACPoller.Ui.ViewModels;

/// <summary>
/// Edition d'une configuration par formulaire.
/// </summary>
/// <remarks>
/// Le principe qui gouverne ce modele : l'arbre JSON charge depuis le service
/// est CONSERVE, et l'enregistrement n'y ecrit que les noeuds connus du
/// formulaire. Un reglage ajoute par une future version du service, ou saisi a
/// la main dans le fichier, survit donc a un passage par cet ecran.
///
/// Reconstruire le JSON depuis les seules proprietes du formulaire serait plus
/// simple a ecrire et effacerait silencieusement tout ce que l'ecran ne connait
/// pas. C'est le genre de perte qu'on ne decouvre qu'en production.
/// </remarks>
public sealed partial class ConfigurationFormViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions FormatOptions = new() { WriteIndented = true };

    private readonly ControlApiClient _client;
    private readonly bool _isNew;
    private readonly bool _isTemplate;

    private JsonObject _root = [];
    private string? _version;

    /// <summary>Construit le formulaire pour une configuration ou un gabarit.</summary>
    /// <param name="client">Client de l'API de pilotage.</param>
    /// <param name="name">Nom, null pour une creation.</param>
    /// <param name="isTemplate">Vrai pour editer un gabarit.</param>
    public ConfigurationFormViewModel(ControlApiClient client, string? name, bool isTemplate = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _isTemplate = isTemplate;
        _isNew = string.IsNullOrWhiteSpace(name);
        _name = name ?? (isTemplate ? "NOUVEAU-GABARIT" : "NOUVELLE-CONFIGURATION");
    }

    /// <summary>Vrai quand l'ecran edite un gabarit.</summary>
    public bool IsTemplate => _isTemplate;

    /// <summary>Vrai quand l'ecran edite une configuration.</summary>
    public bool IsConfiguration => !_isTemplate;

    // ---- Listes de choix ---------------------------------------------------

    /// <summary>Protocoles de collecte disponibles.</summary>
    public static IReadOnlyList<string> Protocols { get; } = ["graph", "imap", "folder"];

    /// <summary>Dossiers canoniques.</summary>
    public static IReadOnlyList<string> WellKnownFolders { get; } =
        ["Inbox", "Archive", "SentItems", "DeletedItems", "None"];

    /// <summary>Actions applicables au message traite.</summary>
    public static IReadOnlyList<string> Dispositions { get; } = ["None", "MarkAsRead", "Move", "Delete"];

    /// <summary>Modes de production des PDF.</summary>
    public static IReadOnlyList<string> PdfModes { get; } = ["Merged", "PerAttachment", "Both"];

    /// <summary>Formats de fichier d'information.</summary>
    public static IReadOnlyList<string> MetadataFormats { get; } = ["json", "xml", "csv", "xslt"];

    /// <summary>Politiques d'echec de conversion.</summary>
    public static IReadOnlyList<string> FailurePolicies { get; } = ["Substitute", "Skip", "RejectMessage"];

    // ---- General -----------------------------------------------------------

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _template = string.Empty;

    [ObservableProperty]
    private int _intervalSeconds = 120;

    [ObservableProperty]
    private int _startupJitterSeconds = 30;

    [ObservableProperty]
    private bool _unreadOnly = true;

    [ObservableProperty]
    private int _maxCount = 200;

    [ObservableProperty]
    private DateTime? _notBefore;

    [ObservableProperty]
    private bool _requireAttachments;

    /// <summary>
    /// Rattrapage au PREMIER cycle, en jours. Contrairement a la date
    /// plancher, il glisse : un redemarrage six mois plus tard rattrape les N
    /// derniers jours a ce moment-la.
    /// </summary>
    [ObservableProperty]
    private int _initialLookbackDays = 7;

    // ---- Protocole ---------------------------------------------------------

    [ObservableProperty]
    private string _protocol = "graph";

    [ObservableProperty]
    private string _mailbox = string.Empty;

    [ObservableProperty]
    private string _folder = "Inbox";

    [ObservableProperty]
    private string _disposition = "MarkAsRead";

    [ObservableProperty]
    private string _targetFolder = "Archive";

    // Graph
    [ObservableProperty]
    private string _tenantId = string.Empty;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _clientSecret = string.Empty;

    [ObservableProperty]
    private string _certificateThumbprint = string.Empty;

    [ObservableProperty]
    private int _throttleMaxConcurrent = 8;

    [ObservableProperty]
    private int _throttleMinSpacingMs = 50;

    [ObservableProperty]
    private bool _honorRetryAfter = true;

    // IMAP
    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 993;

    [ObservableProperty]
    private bool _useSsl = true;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    // Repertoire
    [ObservableProperty]
    private string _folderPath = string.Empty;

    /// <summary>Vrai quand le protocole selectionne est Graph.</summary>
    public bool IsGraph => Protocol == "graph";

    /// <summary>Vrai quand le protocole selectionne est IMAP.</summary>
    public bool IsImap => Protocol == "imap";

    /// <summary>Vrai quand le protocole selectionne est un repertoire local.</summary>
    public bool IsFolder => Protocol == "folder";

    partial void OnProtocolChanged(string value)
    {
        OnPropertyChanged(nameof(IsGraph));
        OnPropertyChanged(nameof(IsImap));
        OnPropertyChanged(nameof(IsFolder));
    }

    // ---- Conversion --------------------------------------------------------

    [ObservableProperty]
    private int _timeoutMinutes = 2;

    [ObservableProperty]
    private string _onFailure = "Substitute";

    [ObservableProperty]
    private bool _includeBody = true;

    [ObservableProperty]
    private bool _keepOriginals;

    [ObservableProperty]
    private int _minInlineImageKilobytes = 20;

    [ObservableProperty]
    private int _maxDepth = 5;

    [ObservableProperty]
    private int _maxNodes = 500;

    [ObservableProperty]
    private long _maxExpandedMegabytes = 512;

    [ObservableProperty]
    private long _maxSingleEntryMegabytes = 128;

    /// <summary>
    /// Extensions ecartees avant conversion, separees par des virgules. Une
    /// liste editable ligne a ligne serait plus lourde pour une poignee de
    /// valeurs qu'on renseigne une fois.
    /// </summary>
    [ObservableProperty]
    private string _excludedExtensions = string.Empty;

    // ---- Sortie ------------------------------------------------------------

    [ObservableProperty]
    private string _pdfMode = "Merged";

    [ObservableProperty]
    private string _metadataFormat = "json";

    [ObservableProperty]
    private string _naming = "{received:yyyyMMdd_HHmmss}_{from}_{guid}";

    [ObservableProperty]
    private string _metadataTemplate = string.Empty;

    [ObservableProperty]
    private string _metadataExtension = string.Empty;

    /// <summary>
    /// Vrai quand le format retenu est une transformation XSLT. La feuille de
    /// style devient alors obligatoire.
    /// </summary>
    public bool IsXslt => MetadataFormat == "xslt";

    partial void OnMetadataFormatChanged(string value) => OnPropertyChanged(nameof(IsXslt));

    [ObservableProperty]
    private bool _atomicWrite = true;

    [ObservableProperty]
    private bool _allOrNothing = true;

    [ObservableProperty]
    private string _sentinelFileName = string.Empty;

    [ObservableProperty]
    private int _maxAttempts = 3;

    [ObservableProperty]
    private int _initialBackoffSeconds = 2;

    /// <summary>Cibles d'export declarees.</summary>
    public ObservableCollection<TargetViewModel> Targets { get; } = [];

    /// <summary>
    /// Gabarits disponibles, precedes d'une entree vide pour "aucun". La
    /// saisie libre laissait passer les fautes de frappe, qui ne se
    /// manifestaient qu'au demarrage du service.
    /// </summary>
    public ObservableCollection<string> AvailableTemplates { get; } = [string.Empty];

    /// <summary>Traitements metier proposes, dans l'ordre d'execution.</summary>
    public ObservableCollection<ProcessorSelection> AvailableProcessors { get; } = [];

    /// <summary>Plugins presents mais non charges, avec leur motif.</summary>
    public ObservableCollection<RejectedPluginDto> RejectedPlugins { get; } = [];

    /// <summary>Vrai si au moins un plugin a ete refuse.</summary>
    public bool HasRejectedPlugins => RejectedPlugins.Count > 0;

    /// <summary>
    /// Champs ajoutes au fichier d'information, dans l'ordre de declaration.
    /// L'ordre fait foi : c'est celui des colonnes du CSV.
    /// </summary>
    public ObservableCollection<MetadataFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    private MetadataFieldViewModel? _selectedField;

    [ObservableProperty]
    private TargetViewModel? _selectedTarget;

    // ---- Etat --------------------------------------------------------------

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _jsonPreview = string.Empty;

    /// <summary>Anomalies renvoyees par le service lors du dernier refus.</summary>
    public ObservableCollection<IssueDto> Issues { get; } = [];

    /// <summary>Vrai si les modifications ont ete enregistrees.</summary>
    public bool Saved { get; private set; }

    /// <summary>Charge la configuration depuis le service.</summary>
    /// <returns>Une tache achevee apres chargement.</returns>
    public async Task LoadAsync()
    {
        IsBusy = true;

        try
        {
            // Les listes de choix sont chargees AVANT le contenu : ReadFromJson
            // coche les processeurs et selectionne le gabarit en s'appuyant
            // dessus. Dans l'ordre inverse, rien ne serait selectionne.
            await LoadChoicesAsync().ConfigureAwait(true);

            if (_isNew)
            {
                if (!_isTemplate)
                {
                    Targets.Add(new TargetViewModel { Type = "fs", Name = "ged" });
                    SelectedTarget = Targets[0];
                }

                SetStatus(
                    _isTemplate
                        ? "Nouveau gabarit. Renseigner ce qui sera commun aux boites qui en heriteront."
                        : "Nouvelle configuration. Renseigner la source et au moins une cible.",
                    isError: false);

                return;
            }

            var raw = _isTemplate
                ? await _client.GetTemplateRawAsync(Name, CancellationToken.None).ConfigureAwait(true)
                : await _client.GetRawAsync(Name, CancellationToken.None).ConfigureAwait(true);

            if (raw?.Configuration is not JsonObject node)
            {
                SetStatus(_isTemplate ? "Gabarit introuvable." : "Configuration introuvable.", isError: true);
                return;
            }

            _version = raw.Version;
            _root = node;
            ReadFromJson(node);

            SetStatus(
                "Les champs de secret sont vides : les laisser ainsi conserve la valeur actuelle.",
                isError: false);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Chargement impossible : {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Charge les gabarits et les traitements metier disponibles.
    /// </summary>
    /// <remarks>
    /// Un echec ici ne bloque PAS l'edition : les valeurs deja enregistrees
    /// restent affichees, et un processeur dont le plugin n'est plus charge
    /// apparaitra simplement comme manquant. Refuser d'ouvrir l'ecran parce
    /// qu'une liste de choix est indisponible serait disproportionne.
    /// </remarks>
    private async Task LoadChoicesAsync()
    {
        try
        {
            // Un gabarit n'herite pas d'un autre gabarit : un seul niveau, pas
            // de chainage. Autoriser la chaine rendrait la resolution
            // difficile a suivre pour un gain douteux.
            if (!_isTemplate)
            {
                var templates = await _client.GetTemplatesAsync(CancellationToken.None).ConfigureAwait(true);

                foreach (var template in templates)
                {
                    AvailableTemplates.Add(template.Name);
                }
            }

            var available = await _client.GetProcessorsAsync(CancellationToken.None).ConfigureAwait(true);

            foreach (var processor in available.Processors.OrderBy(p => p.Order))
            {
                AvailableProcessors.Add(new ProcessorSelection
                {
                    Name = processor.Name,
                    Stages = processor.StagesLabel,
                    Order = processor.Order,
                });
            }

            foreach (var rejected in available.RejectedPlugins)
            {
                RejectedPlugins.Add(rejected);
            }

            OnPropertyChanged(nameof(HasRejectedPlugins));
        }
        catch (HttpRequestException)
        {
            // Voir la remarque de methode.
        }
    }

    /// <summary>Ajoute une cible d'export.</summary>
    [RelayCommand]
    public void AddTarget()
    {
        var target = new TargetViewModel { Type = "fs", Name = $"cible{Targets.Count + 1}" };
        Targets.Add(target);
        SelectedTarget = target;
    }

    /// <summary>Retire la cible selectionnee.</summary>
    [RelayCommand]
    public void RemoveTarget()
    {
        if (SelectedTarget is not null)
        {
            Targets.Remove(SelectedTarget);
            SelectedTarget = Targets.FirstOrDefault();
        }
    }

    /// <summary>Ajoute un reglage libre a la cible selectionnee.</summary>
    [RelayCommand]
    public void AddSetting() => SelectedTarget?.Settings.Add(new SettingEntry());

    /// <summary>Ajoute un champ au fichier d'information.</summary>
    [RelayCommand]
    public void AddField()
    {
        var field = new MetadataFieldViewModel { Name = $"Champ{Fields.Count + 1}" };
        Fields.Add(field);
        SelectedField = field;
    }

    /// <summary>Retire le champ selectionne.</summary>
    [RelayCommand]
    public void RemoveField()
    {
        if (SelectedField is not null)
        {
            Fields.Remove(SelectedField);
            SelectedField = Fields.FirstOrDefault();
        }
    }

    /// <summary>
    /// Coche les traitements metier declares.
    /// </summary>
    /// <remarks>
    /// Un processeur declare mais absent des disponibles est CONSERVE et
    /// signale : le supprimer silencieusement ferait perdre le parametrage au
    /// premier enregistrement, alors que le plugin peut simplement avoir ete
    /// retire le temps d'une mise a jour.
    /// </remarks>
    private void ReadProcessors(JsonObject node)
    {
        var declared = (node["Processors"] as JsonArray ?? [])
            .Select(p => p?.GetValue<string>() ?? string.Empty)
            .Where(p => p.Length > 0)
            .ToArray();

        foreach (var processor in AvailableProcessors)
        {
            processor.IsSelected = declared.Contains(processor.Name, StringComparer.OrdinalIgnoreCase);
        }

        var missing = declared.Where(d => !AvailableProcessors.Any(p =>
            string.Equals(p.Name, d, StringComparison.OrdinalIgnoreCase)));

        foreach (var name in missing)
        {
            AvailableProcessors.Add(new ProcessorSelection
            {
                Name = name,
                Stages = "plugin absent ou refuse",
                IsMissing = true,
                IsSelected = true,
            });
        }
    }

    /// <summary>Remonte le champ selectionne dans l'ordre des colonnes.</summary>
    [RelayCommand]
    public void MoveFieldUp()
    {
        var index = SelectedField is null ? -1 : Fields.IndexOf(SelectedField);

        if (index > 0)
        {
            Fields.Move(index, index - 1);
        }
    }

    /// <summary>Descend le champ selectionne dans l'ordre des colonnes.</summary>
    [RelayCommand]
    public void MoveFieldDown()
    {
        var index = SelectedField is null ? -1 : Fields.IndexOf(SelectedField);

        if (index >= 0 && index < Fields.Count - 1)
        {
            Fields.Move(index, index + 1);
        }
    }

    /// <summary>Ajoute une traduction de valeur au champ selectionne.</summary>
    [RelayCommand]
    public void AddValueMapping() => SelectedField?.Values.Add(new ValueMapping());

    /// <summary>Choisit la feuille de style par une boite de dialogue.</summary>
    [RelayCommand]
    public void BrowseStylesheet()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Feuille de transformation",
            Filter = "Feuilles XSLT (*.xslt;*.xsl)|*.xslt;*.xsl|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
        };

        // Le chemin est celui du SERVEUR, pas du poste : la boite de dialogue
        // n'est qu'une aide a la saisie, et un chemin local ne vaut que si
        // l'UI tourne sur le serveur. Le champ reste editable a la main.
        if (dialog.ShowDialog() == true)
        {
            MetadataTemplate = dialog.FileName;
        }
    }

    /// <summary>Rafraichit l'apercu JSON depuis l'etat du formulaire.</summary>
    [RelayCommand]
    public void RefreshPreview() => JsonPreview = BuildJson().ToJsonString(FormatOptions);

    /// <summary>Enregistre la configuration sur le service.</summary>
    /// <returns>Une tache achevee apres enregistrement.</returns>
    [RelayCommand]
    public async Task SaveAsync()
    {
        Issues.Clear();
        IsBusy = true;

        try
        {
            var result = _isTemplate
                ? await _client
                    .UpsertTemplateAsync(Name.Trim(), BuildJson(), _version, CancellationToken.None)
                    .ConfigureAwait(true)
                : await _client
                    .UpsertAsync(Name.Trim(), BuildJson(), _version, CancellationToken.None)
                    .ConfigureAwait(true);

            foreach (var issue in result.Issues)
            {
                Issues.Add(issue);
            }

            if (!result.Success)
            {
                SetStatus(result.Message, isError: true);
                return;
            }

            _version = result.Version;
            Saved = true;
            SetStatus(result.Message, isError: false);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Enregistrement impossible : {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReadFromJson(JsonObject node)
    {
        Enabled = node["Enabled"]?.GetValue<bool>() ?? true;
        Template = node["Template"]?.GetValue<string>() ?? string.Empty;

        if (node["Source"] is JsonObject source)
        {
            Protocol = source["Protocol"]?.GetValue<string>() ?? "graph";
            Mailbox = source["Mailbox"]?.GetValue<string>() ?? string.Empty;
            Disposition = source["Disposition"]?.GetValue<string>() ?? "MarkAsRead";
            TenantId = source["TenantId"]?.GetValue<string>() ?? string.Empty;
            ClientId = source["ClientId"]?.GetValue<string>() ?? string.Empty;
            CertificateThumbprint = source["CertificateThumbprint"]?.GetValue<string>() ?? string.Empty;
            Host = source["Host"]?.GetValue<string>() ?? string.Empty;
            Port = source["Port"]?.GetValue<int>() ?? 993;
            UseSsl = source["UseSsl"]?.GetValue<bool>() ?? true;
            UserName = source["UserName"]?.GetValue<string>() ?? string.Empty;
            FolderPath = source["Path"]?.GetValue<string>() ?? string.Empty;

            if (source["Throttling"] is JsonObject throttling)
            {
                ThrottleMaxConcurrent = throttling["MaxConcurrent"]?.GetValue<int>() ?? 8;
                ThrottleMinSpacingMs = throttling["MinSpacingMs"]?.GetValue<int>() ?? 50;
                HonorRetryAfter = throttling["HonorRetryAfter"]?.GetValue<bool>() ?? true;
            }

            Folder = (source["Folder"] as JsonObject)?["WellKnown"]?.GetValue<string>() ?? "Inbox";
            TargetFolder = (source["TargetFolder"] as JsonObject)?["WellKnown"]?.GetValue<string>() ?? "Archive";
        }

        if (node["Query"] is JsonObject query)
        {
            UnreadOnly = query["UnreadOnly"]?.GetValue<bool>() ?? true;
            MaxCount = query["MaxCount"]?.GetValue<int>() ?? 200;
            RequireAttachments = query["RequireAttachments"]?.GetValue<bool>() ?? false;
            InitialLookbackDays = query["InitialLookbackDays"]?.GetValue<int>() ?? 7;

            NotBefore = DateTime.TryParse(
                query["NotBefore"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var floor) ? floor : null;
        }

        if (node["Schedule"] is JsonObject schedule)
        {
            IntervalSeconds = schedule["IntervalSeconds"]?.GetValue<int>() ?? 120;
            StartupJitterSeconds = schedule["StartupJitterSeconds"]?.GetValue<int>() ?? 30;
        }

        if (node["Extraction"] is JsonObject extraction)
        {
            MaxDepth = extraction["MaxDepth"]?.GetValue<int>() ?? 5;
            MaxNodes = extraction["MaxNodes"]?.GetValue<int>() ?? 500;

            MaxExpandedMegabytes =
                (extraction["MaxExpandedBytes"]?.GetValue<long>() ?? 536870912) / 1024 / 1024;

            MaxSingleEntryMegabytes =
                (extraction["MaxSingleEntryBytes"]?.GetValue<long>() ?? 134217728) / 1024 / 1024;
        }

        if (node["Conversion"] is JsonObject conversion)
        {
            OnFailure = conversion["OnFailure"]?.GetValue<string>() ?? "Substitute";
            IncludeBody = conversion["IncludeBody"]?.GetValue<bool>() ?? true;
            KeepOriginals = conversion["KeepOriginals"]?.GetValue<bool>() ?? false;

            ExcludedExtensions = string.Join(", ",
                (conversion["ExcludedExtensions"] as JsonArray ?? [])
                    .Select(e => e?.GetValue<string>() ?? string.Empty)
                    .Where(e => e.Length > 0));

            MinInlineImageKilobytes =
                (int)((conversion["MinInlineImageBytes"]?.GetValue<long>() ?? 20480) / 1024);

            if (TimeSpan.TryParse(
                conversion["Timeout"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                out var timeout))
            {
                TimeoutMinutes = (int)Math.Max(1, timeout.TotalMinutes);
            }
        }

        if (node["Output"] is JsonObject output)
        {
            PdfMode = output["PdfMode"]?.GetValue<string>() ?? "Merged";
            Naming = output["Naming"]?.GetValue<string>() ?? Naming;
            var metadata = output["Metadata"] as JsonObject;
            MetadataFormat = metadata?["Format"]?.GetValue<string>() ?? "json";
            MetadataTemplate = metadata?["Template"]?.GetValue<string>() ?? string.Empty;
            MetadataExtension = metadata?["Extension"]?.GetValue<string>() ?? string.Empty;

            Fields.Clear();

            foreach (var field in (metadata?["Fields"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Fields.Add(MetadataFieldViewModel.FromJson(field));
            }

            SelectedField = Fields.FirstOrDefault();

            if (output["Policy"] is JsonObject policy)
            {
                AtomicWrite = policy["AtomicWrite"]?.GetValue<bool>() ?? true;
                AllOrNothing = policy["AllOrNothing"]?.GetValue<bool>() ?? true;
                SentinelFileName = policy["SentinelFileName"]?.GetValue<string>() ?? string.Empty;
                MaxAttempts = policy["MaxAttempts"]?.GetValue<int>() ?? 3;
                InitialBackoffSeconds = policy["InitialBackoffSeconds"]?.GetValue<int>() ?? 2;
            }

            Targets.Clear();

            foreach (var target in (output["Targets"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Targets.Add(TargetViewModel.FromJson(target));
            }

            SelectedTarget = Targets.FirstOrDefault();
        }

        ReadProcessors(node);
    }

    /// <summary>
    /// Ecrit l'etat du formulaire dans l'arbre charge, sans le reconstruire.
    /// </summary>
    /// <remarks>
    /// Les noeuds absents sont crees, les autres modifies en place. Tout ce que
    /// le formulaire ne connait pas reste intact : c'est ce qui permet a un
    /// reglage saisi a la main de survivre a un passage par cet ecran.
    /// </remarks>
    private JsonObject BuildJson()
    {
        var node = _root.DeepClone().AsObject();

        node["Name"] = Name.Trim();
        node["Enabled"] = Enabled;

        if (string.IsNullOrWhiteSpace(Template))
        {
            node.Remove("Template");
        }
        else
        {
            node["Template"] = Template.Trim();
        }

        var source = Ensure(node, "Source");
        source["Protocol"] = Protocol;
        source["Mailbox"] = Mailbox.Trim();
        source["Disposition"] = Disposition;
        Ensure(source, "Folder")["WellKnown"] = Folder;

        if (Disposition == "Move")
        {
            Ensure(source, "TargetFolder")["WellKnown"] = TargetFolder;
        }

        switch (Protocol)
        {
            case "graph":
                source["TenantId"] = TenantId.Trim();
                source["ClientId"] = ClientId.Trim();
                source["CertificateThumbprint"] = CertificateThumbprint.Trim();

                // Secret vide : le service conserve la valeur chiffree existante.
                // Ne PAS ecrire une chaine vide serait pire, le champ
                // disparaitrait de l'arbre et le report ne trouverait rien.
                source["ClientSecret"] = ClientSecret;
                break;

            case "imap":
                source["Host"] = Host.Trim();
                source["Port"] = Port;
                source["UseSsl"] = UseSsl;
                source["UserName"] = UserName.Trim();
                source["Password"] = Password;
                break;

            case "folder":
                source["Path"] = FolderPath.Trim();
                break;

            default:
                break;
        }

        // La limitation ne concerne que Graph : l'ecrire pour IMAP encombrerait
        // le fichier d'un reglage sans effet.
        if (Protocol == "graph")
        {
            var throttling = Ensure(source, "Throttling");
            throttling["MaxConcurrent"] = ThrottleMaxConcurrent;
            throttling["MinSpacingMs"] = ThrottleMinSpacingMs;
            throttling["HonorRetryAfter"] = HonorRetryAfter;
        }
        else
        {
            source.Remove("Throttling");
        }

        var query = Ensure(node, "Query");
        query["UnreadOnly"] = UnreadOnly;
        query["MaxCount"] = MaxCount;
        query["RequireAttachments"] = RequireAttachments;
        query["InitialLookbackDays"] = InitialLookbackDays;
        query["NotBefore"] = NotBefore?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var schedule = Ensure(node, "Schedule");
        schedule["IntervalSeconds"] = IntervalSeconds;
        schedule["StartupJitterSeconds"] = StartupJitterSeconds;

        var extraction = Ensure(node, "Extraction");
        extraction["MaxDepth"] = MaxDepth;
        extraction["MaxNodes"] = MaxNodes;
        extraction["MaxExpandedBytes"] = MaxExpandedMegabytes * 1024 * 1024;
        extraction["MaxSingleEntryBytes"] = MaxSingleEntryMegabytes * 1024 * 1024;

        var conversion = Ensure(node, "Conversion");
        conversion["Timeout"] = TimeSpan.FromMinutes(TimeoutMinutes).ToString("c", CultureInfo.InvariantCulture);
        conversion["OnFailure"] = OnFailure;
        conversion["IncludeBody"] = IncludeBody;
        conversion["KeepOriginals"] = KeepOriginals;
        conversion["MinInlineImageBytes"] = (long)MinInlineImageKilobytes * 1024;

        var excluded = new JsonArray();

        foreach (var extension in ExcludedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Le point est ajoute s'il manque : l'exploitant saisit
            // naturellement "p7s" ou ".p7s", et le filtre compare a
            // Path.GetExtension, qui renvoie toujours le point.
            excluded.Add(extension.StartsWith('.') ? extension : "." + extension);
        }

        if (excluded.Count > 0)
        {
            conversion["ExcludedExtensions"] = excluded;
        }
        else
        {
            conversion.Remove("ExcludedExtensions");
        }

        var output = Ensure(node, "Output");
        output["PdfMode"] = PdfMode;
        output["Naming"] = Naming;
        var metadataNode = Ensure(output, "Metadata");
        metadataNode["Format"] = MetadataFormat;

        // Proprietes vides retirees plutot qu'ecrites vides : le service
        // applique ses defauts, et un fichier lisible vaut mieux qu'un fichier
        // exhaustif.
        if (string.IsNullOrWhiteSpace(MetadataTemplate))
        {
            metadataNode.Remove("Template");
        }
        else
        {
            metadataNode["Template"] = MetadataTemplate.Trim();
        }

        if (string.IsNullOrWhiteSpace(MetadataExtension))
        {
            metadataNode.Remove("Extension");
        }
        else
        {
            metadataNode["Extension"] = MetadataExtension.Trim().TrimStart('.');
        }

        var fieldArray = new JsonArray();

        foreach (var field in Fields.Where(f => !string.IsNullOrWhiteSpace(f.Name)))
        {
            fieldArray.Add(field.ToJson());
        }

        if (fieldArray.Count > 0)
        {
            metadataNode["Fields"] = fieldArray;
        }
        else
        {
            metadataNode.Remove("Fields");
        }

        var policy = Ensure(output, "Policy");
        policy["AtomicWrite"] = AtomicWrite;
        policy["AllOrNothing"] = AllOrNothing;
        policy["MaxAttempts"] = MaxAttempts;
        policy["InitialBackoffSeconds"] = InitialBackoffSeconds;

        if (string.IsNullOrWhiteSpace(SentinelFileName))
        {
            policy.Remove("SentinelFileName");
        }
        else
        {
            policy["SentinelFileName"] = SentinelFileName.Trim();
        }

        var targets = new JsonArray();

        foreach (var target in Targets)
        {
            targets.Add(target.ToJson());
        }

        output["Targets"] = targets;

        var processors = new JsonArray();

        foreach (var processor in AvailableProcessors.Where(p => p.IsSelected).OrderBy(p => p.Order))
        {
            processors.Add(processor.Name);
        }

        if (processors.Count > 0)
        {
            node["Processors"] = processors;
        }
        else
        {
            node.Remove("Processors");
        }

        return node;
    }

    private static JsonObject Ensure(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
