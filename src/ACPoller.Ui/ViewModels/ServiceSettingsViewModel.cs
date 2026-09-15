using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json.Nodes;
using ACPoller.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACPoller.Ui.ViewModels;

/// <summary>
/// Reglages de service, non rechargeables a chaud.
/// </summary>
/// <remarks>
/// Meme principe que le formulaire de configuration : l'arbre JSON charge est
/// CONSERVE, et l'enregistrement n'y ecrit que les noeuds connus. Un reglage
/// ajoute par une future version du service survit donc a un passage par cet
/// ecran.
///
/// Tout ce qui est modifie ici exige un redemarrage. L'ecran le dit avant la
/// saisie, pas apres l'enregistrement : prevenir apres coup laisse
/// l'exploitant croire que sa modification est active.
/// </remarks>
public sealed partial class ServiceSettingsViewModel(ControlApiClient client) : ObservableObject
{
    private JsonObject _root = [];
    private string? _version;

    // ---- Service -----------------------------------------------------------

    [ObservableProperty]
    private string _workDirectory = string.Empty;

    [ObservableProperty]
    private int _maxGlobalConcurrency = 4;

    [ObservableProperty]
    private string _pluginDirectory = string.Empty;

    [ObservableProperty]
    private int _processedRetentionDays = 90;

    // ---- Outils ------------------------------------------------------------

    [ObservableProperty]
    private string _sofficePath = string.Empty;

    [ObservableProperty]
    private int _libreOfficeProfiles = 2;

    [ObservableProperty]
    private string _qpdfPath = string.Empty;

    [ObservableProperty]
    private string _fontDirectory = string.Empty;

    // ---- Maintenance -------------------------------------------------------

    [ObservableProperty]
    private int _maintenanceIntervalMinutes = 60;

    [ObservableProperty]
    private int _quarantineAfterDays = 7;

    [ObservableProperty]
    private int _lowDiskThresholdPercent = 10;

    // ---- API de pilotage ---------------------------------------------------

    [ObservableProperty]
    private bool _controlApiEnabled = true;

    [ObservableProperty]
    private int _controlApiPort = 5199;

    [ObservableProperty]
    private string _controlApiToken = string.Empty;

    // ---- Metriques ---------------------------------------------------------

    [ObservableProperty]
    private string _metricsConnectionString = string.Empty;

    // ---- Etat --------------------------------------------------------------

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Anomalies renvoyees par le service lors du dernier refus.</summary>
    public ObservableCollection<IssueDto> Issues { get; } = [];

    /// <summary>Vrai si les reglages ont ete enregistres.</summary>
    public bool Saved { get; private set; }

    /// <summary>Charge les reglages depuis le service.</summary>
    /// <returns>Une tache achevee apres chargement.</returns>
    public async Task LoadAsync()
    {
        IsBusy = true;

        try
        {
            var raw = await client.GetServiceSettingsAsync(CancellationToken.None).ConfigureAwait(true);

            if (raw?.Settings is not JsonObject node)
            {
                SetStatus("Reglages introuvables.", isError: true);
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

    /// <summary>Choisit un fichier par une boite de dialogue.</summary>
    /// <param name="target">Champ a renseigner : soffice, qpdf ou fonts.</param>
    [RelayCommand]
    public void Browse(string target)
    {
        // Le chemin est celui du SERVEUR : la boite de dialogue n'est qu'une
        // aide a la saisie, et ne vaut que si l'interface tourne sur le
        // serveur. Les champs restent editables a la main.
        if (target == "fonts")
        {
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = "Repertoire des polices" };

            if (folder.ShowDialog() == true)
            {
                FontDirectory = folder.FolderName;
            }

            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = target == "soffice" ? "Executable LibreOffice" : "Executable qpdf",
            Filter = "Executables (*.exe)|*.exe|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (target == "soffice")
        {
            SofficePath = dialog.FileName;
        }
        else
        {
            QpdfPath = dialog.FileName;
        }
    }

    /// <summary>Enregistre les reglages.</summary>
    /// <returns>Une tache achevee apres enregistrement.</returns>
    [RelayCommand]
    public async Task SaveAsync()
    {
        Issues.Clear();
        IsBusy = true;

        try
        {
            var result = await client
                .UpdateServiceSettingsAsync(BuildJson(), _version, CancellationToken.None)
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

            SetStatus(
                "Enregistre. Ces reglages ne prendront effet qu'apres redemarrage du service.",
                isError: false);
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
        if (node["Poller"] is JsonObject poller)
        {
            WorkDirectory = poller["WorkDirectory"]?.GetValue<string>() ?? string.Empty;
            MaxGlobalConcurrency = poller["MaxGlobalConcurrency"]?.GetValue<int>() ?? 4;
            PluginDirectory = poller["PluginDirectory"]?.GetValue<string>() ?? string.Empty;
            ProcessedRetentionDays = poller["ProcessedRetentionDays"]?.GetValue<int>() ?? 90;
        }

        if (node["Conversion"] is JsonObject conversion)
        {
            if (conversion["LibreOffice"] is JsonObject libreOffice)
            {
                SofficePath = libreOffice["SofficePath"]?.GetValue<string>() ?? string.Empty;
                LibreOfficeProfiles = libreOffice["ProfileCount"]?.GetValue<int>() ?? 2;
            }

            QpdfPath = (conversion["Pdf"] as JsonObject)?["QpdfPath"]?.GetValue<string>() ?? string.Empty;
            FontDirectory = (conversion["Fonts"] as JsonObject)?["FontDirectory"]?.GetValue<string>()
                ?? string.Empty;
        }

        if (node["Maintenance"] is JsonObject maintenance)
        {
            MaintenanceIntervalMinutes = maintenance["IntervalMinutes"]?.GetValue<int>() ?? 60;
            QuarantineAfterDays = maintenance["QuarantineAfterDays"]?.GetValue<int>() ?? 7;
            LowDiskThresholdPercent = maintenance["LowDiskThresholdPercent"]?.GetValue<int>() ?? 10;
        }

        if (node["ControlApi"] is JsonObject controlApi)
        {
            ControlApiEnabled = controlApi["Enabled"]?.GetValue<bool>() ?? true;
            ControlApiPort = controlApi["Port"]?.GetValue<int>() ?? 5199;
        }
    }

    private JsonObject BuildJson()
    {
        var node = _root.DeepClone().AsObject();

        var poller = Ensure(node, "Poller");
        poller["WorkDirectory"] = WorkDirectory.Trim();
        poller["MaxGlobalConcurrency"] = MaxGlobalConcurrency;
        poller["PluginDirectory"] = PluginDirectory.Trim();
        poller["ProcessedRetentionDays"] = ProcessedRetentionDays;

        var conversion = Ensure(node, "Conversion");
        var libreOffice = Ensure(conversion, "LibreOffice");
        libreOffice["SofficePath"] = SofficePath.Trim();
        libreOffice["ProfileCount"] = LibreOfficeProfiles;

        Ensure(conversion, "Pdf")["QpdfPath"] = QpdfPath.Trim();
        Ensure(conversion, "Fonts")["FontDirectory"] = FontDirectory.Trim();

        var maintenance = Ensure(node, "Maintenance");
        maintenance["IntervalMinutes"] = MaintenanceIntervalMinutes;
        maintenance["QuarantineAfterDays"] = QuarantineAfterDays;
        maintenance["LowDiskThresholdPercent"] = LowDiskThresholdPercent;

        var controlApi = Ensure(node, "ControlApi");
        controlApi["Enabled"] = ControlApiEnabled;
        controlApi["Port"] = ControlApiPort;

        // Secrets ecrits seulement s'ils ont ete saisis : vide signifie
        // inchange, et le service conserve alors la valeur chiffree.
        if (!string.IsNullOrWhiteSpace(ControlApiToken))
        {
            controlApi["Token"] = ControlApiToken.Trim();
        }
        else
        {
            controlApi["Token"] = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(MetricsConnectionString))
        {
            Ensure(Ensure(node, "Metrics"), "SqlServer")["ConnectionString"] =
                MetricsConnectionString.Trim();
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
