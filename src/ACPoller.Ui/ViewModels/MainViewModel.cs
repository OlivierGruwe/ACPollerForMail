using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using ACPoller.Ui.Services;
using ACPoller.Ui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ACPoller.Ui.ViewModels;

/// <summary>
/// Etat de l'ecran de supervision.
/// </summary>
/// <remarks>
/// L'UI n'ecrit RIEN directement dans la configuration du service : elle lit
/// l'etat et declenche des commandes, toute modification passant par l'API.
/// Le service reste seul maitre de ce qu'il lit et de quand il le relit. Une
/// UI qui ecrirait le fichier pendant qu'un worker le relit produirait un etat
/// incoherent, sans erreur.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly PeriodicTimer _refreshTimer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Vrai si le service attend un redemarrage.</summary>
    [ObservableProperty]
    private bool _restartRequired;

    /// <summary>Reglages en attente de redemarrage.</summary>
    public ObservableCollection<PendingRestartDto> PendingRestart { get; } = [];

    // Non readonly : l'ecran de reglages remplace les deux quand l'adresse ou
    // le jeton changent. HttpClient porte l'adresse de base et l'en-tete
    // d'authentification, les modifier demande une nouvelle instance.
    private ControlApiClient _client;
    private ClientSettings _settings;
    private bool _suppressThemePersistence;
    /// <summary>Tableau de bord d'exploitation.</summary>
    public DashboardViewModel Dashboard { get; private set; }

    [ObservableProperty]
    private ConfigurationDto? _selectedConfiguration;

    [ObservableProperty]
    private TemplateDto? _selectedTemplate;

    [ObservableProperty]
    private ServiceStatusDto? _status;

    [ObservableProperty]
    private string _connectionState = "Connexion...";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>Construit le modele et lance le rafraichissement periodique.</summary>
    public MainViewModel()
    {
        _settings = ClientSettings.Load();
        _client = new ControlApiClient(_settings);

        Dashboard = new DashboardViewModel(_client);

        // Affectation du CHAMP et non de la propriete : passer par la propriete
        // declencherait OnIsDarkThemeChanged, qui reecrirait les reglages au
        // demarrage et transformerait un theme "Systeme" en valeur figee.
        _isDarkTheme = ThemeService.IsDark;

        _ = RefreshLoopAsync();
    }

    /// <summary>Configurations connues du service.</summary>
    public ObservableCollection<ConfigurationDto> Configurations { get; } = [];

    /// <summary>Gabarits declares, avec leur portee.</summary>
    public ObservableCollection<TemplateDto> Templates { get; } = [];

    /// <summary>Etat des workers.</summary>
    public ObservableCollection<WorkerStatusDto> Workers { get; } = [];

    /// <summary>Resultats du dernier test de connexion.</summary>
    public ObservableCollection<ComponentCheckDto> TestResults { get; } = [];


    // ---- Lecture -----------------------------------------------------------

    /// <summary>Recharge l'etat, les configurations et les gabarits.</summary>
    /// <returns>Une tache achevee apres rafraichissement.</returns>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var status = await _client.GetStatusAsync(_cts.Token).ConfigureAwait(true);
            var configurations = await _client.GetConfigurationsAsync(_cts.Token).ConfigureAwait(true);
            var templates = await _client.GetTemplatesAsync(_cts.Token).ConfigureAwait(true);

            Status = status;

            RestartRequired = status?.RestartRequired ?? false;

            PendingRestart.Clear();

            foreach (var pending in status?.PendingRestart ?? [])
            {
                PendingRestart.Add(pending);
            }

            IsConnected = true;

            // Message portant l'information qui compte en exploitation :
            // combien de workers tournent, et l'espace disque restant.
            var running = status?.Workers.Count(w => w.State is "Running" or "Idle") ?? 0;
            var disk = status?.FreeDiskPercent is { } percent ? $", disque {percent} % libres" : string.Empty;

            ConnectionState = $"Service {status?.Version} — {running} worker(s) actif(s){disk}";

            Workers.Clear();

            foreach (var worker in status?.Workers ?? [])
            {
                Workers.Add(worker);
            }

            // Les selections sont preservees entre deux rafraichissements,
            // sinon l'ecran devient inutilisable des que le minuteur passe.
            var selectedConfiguration = SelectedConfiguration?.Name;
            Configurations.Clear();

            foreach (var configuration in configurations)
            {
                Configurations.Add(configuration);
            }

            SelectedConfiguration = Configurations.FirstOrDefault(c => c.Name == selectedConfiguration)
                ?? Configurations.FirstOrDefault();

            var selectedTemplate = SelectedTemplate?.Name;
            Templates.Clear();

            foreach (var template in templates)
            {
                Templates.Add(template);
            }

            SelectedTemplate = Templates.FirstOrDefault(t => t.Name == selectedTemplate);

            // Rafraichi avec le reste : le tableau de bord et l'ecran de
            // supervision doivent montrer le meme instant, sinon les chiffres
            // et les etats des workers se contredisent a l'ecran.
            await Dashboard.RefreshAsync(_cts.Token).ConfigureAwait(true);

        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Message explicite : un 401 renvoie vers le jeton, pas vers le
            // service. C'est la confusion qui coute le plus de temps.
            IsConnected = false;
            ConnectionState = "Jeton refuse par le service. Ouvrir les reglages pour le corriger.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsConnected = false;
            ConnectionState = $"Service injoignable : {ex.Message}";
        }
    }

    // ---- Commandes sur une configuration -----------------------------------

    /// <summary>Teste la source et les cibles de la configuration selectionnee.</summary>
    /// <returns>Une tache achevee a la fin des tests.</returns>
    [RelayCommand]
    public async Task TestAsync()
    {
        if (SelectedConfiguration is null)
        {
            return;
        }

        IsBusy = true;
        TestResults.Clear();

        try
        {
            var results = await _client.TestAsync(SelectedConfiguration.Name, _cts.Token).ConfigureAwait(true);

            foreach (var result in results)
            {
                TestResults.Add(result);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            TestResults.Add(new ComponentCheckDto
            {
                Component = "service",
                Success = false,
                Message = ex.Message,
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Redemarre le worker de la configuration selectionnee.</summary>
    /// <returns>Une tache achevee apres redemarrage.</returns>
    [RelayCommand]
    public async Task RestartAsync()
    {
        if (SelectedConfiguration is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _client.RestartWorkerAsync(SelectedConfiguration.Name, _cts.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ConnectionState = $"Redemarrage impossible : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ouvre l'editeur sur la configuration selectionnee.</summary>
    /// <returns>Une tache achevee apres fermeture de l'editeur.</returns>
    [RelayCommand]
    public async Task EditConfigurationAsync()
    {
        if (SelectedConfiguration is not null)
        {
            await OpenEditorAsync(SelectedConfiguration.Name, isTemplate: false).ConfigureAwait(true);
        }
    }

    /// <summary>Ouvre l'editeur sur une nouvelle configuration.</summary>
    /// <returns>Une tache achevee apres fermeture de l'editeur.</returns>
    [RelayCommand]
    public Task NewConfigurationAsync() => OpenEditorAsync(null, isTemplate: false);

    /// <summary>Supprime la configuration selectionnee, apres confirmation.</summary>
    /// <returns>Une tache achevee apres suppression.</returns>
    [RelayCommand]
    public async Task DeleteConfigurationAsync()
    {
        if (SelectedConfiguration is null)
        {
            return;
        }

        var name = SelectedConfiguration.Name;

        // Confirmation nommant la configuration : supprimer la mauvaise boite
        // dans une liste de cent est vite arrive.
        var answer = MessageBox.Show(
            $"Supprimer definitivement la configuration '{name}' ?\n\n"
            + "Le worker sera arrete. Une sauvegarde du fichier est conservee.",
            "ACPoller",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var raw = await _client.GetRawAsync(name, _cts.Token).ConfigureAwait(true);
            var result = await _client
                .DeleteConfigurationAsync(name, raw?.Version, _cts.Token)
                .ConfigureAwait(true);

            ConnectionState = result.Message;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ConnectionState = $"Suppression impossible : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Commandes sur un gabarit ------------------------------------------

    /// <summary>Ouvre l'editeur sur un nouveau gabarit.</summary>
    /// <returns>Une tache achevee apres fermeture de l'editeur.</returns>
    [RelayCommand]
    public Task NewTemplateAsync() => OpenEditorAsync(null, isTemplate: true);

    /// <summary>Ouvre l'editeur sur le gabarit selectionne.</summary>
    /// <returns>Une tache achevee apres fermeture de l'editeur.</returns>
    [RelayCommand]
    public async Task EditTemplateAsync()
    {
        if (SelectedTemplate is not null)
        {
            await OpenEditorAsync(SelectedTemplate.Name, isTemplate: true).ConfigureAwait(true);
        }
    }

    /// <summary>Supprime le gabarit selectionne, apres confirmation.</summary>
    /// <returns>Une tache achevee apres suppression.</returns>
    [RelayCommand]
    public async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        // Le service refuse deja la suppression d'un gabarit utilise, mais
        // autant l'annoncer ici plutot que de laisser l'exploitant declencher
        // une action qui sera refusee.
        if (SelectedTemplate.UsedBy.Count > 0)
        {
            MessageBox.Show(
                $"Le gabarit '{SelectedTemplate.Name}' est utilise par "
                + $"{SelectedTemplate.UsedBy.Count} configuration(s) :\n\n"
                + string.Join("\n", SelectedTemplate.UsedBy)
                + "\n\nLes detacher avant de le supprimer.",
                "ACPoller",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var answer = MessageBox.Show(
            $"Supprimer le gabarit '{SelectedTemplate.Name}' ?",
            "ACPoller",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _client
                .DeleteTemplateAsync(SelectedTemplate.Name, null, _cts.Token)
                .ConfigureAwait(true);

            ConnectionState = result.Message;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ConnectionState = $"Suppression impossible : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Commandes globales ------------------------------------------------

    /// <summary>Declenche une passe de maintenance immediate.</summary>
    /// <returns>Une tache achevee a la fin de la passe.</returns>
    [RelayCommand]
    public async Task RunMaintenanceAsync()
    {
        IsBusy = true;

        try
        {
            await _client.RunMaintenanceAsync(_cts.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ConnectionState = $"Maintenance impossible : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ouvre l'aide de reference.</summary>
    [RelayCommand]
    public static void OpenHelp()
    {
        // Non modale : l'exploitant doit pouvoir garder la liste des jetons
        // sous les yeux pendant qu'il edite une configuration.
        var window = new HelpWindow { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Affiche le detail des reglages en attente de redemarrage.</summary>
    [RelayCommand]
    public void ShowRestartDetail()
    {
        if (PendingRestart.Count == 0)
        {
            return;
        }

        var detail = string.Join(
            "\n\n",
            PendingRestart.Select(p => $"{p.Path}\n{p.Reason}"));

        // L'interface ne redemarre PAS le service elle-meme : elle dialogue
        // avec lui par son API, et un service qui s'arrete sur ordre de son
        // propre client ne pourrait pas confirmer l'ordre execute. C'est au
        // gestionnaire de services de le faire.
        MessageBox.Show(
            "Ces reglages ont ete modifies mais ne prendront effet qu'apres "
            + "redemarrage du service :\n\n"
            + detail
            + "\n\nRedemarrer depuis le gestionnaire de services Windows, ou :\n"
            + "    Restart-Service ACPoller",
            "Redemarrage necessaire",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>Ouvre les reglages de service.</summary>
    /// <returns>Une tache achevee apres fermeture.</returns>
    [RelayCommand]
    public async Task OpenServiceSettingsAsync()
    {
        var window = new ServiceSettingsWindow(_client)
        {
            Owner = Application.Current.MainWindow,
        };

        IsBusy = true;

        try
        {
            if (window.ShowDialog() == true)
            {
                // Le rafraichissement fait apparaitre le bandeau de
                // redemarrage : le service vient de marquer ce qui attend.
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ouvre l'ecran de reglages et reprend la connexion si besoin.</summary>
    /// <returns>Une tache achevee apres rafraichissement.</returns>
    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        var window = new SettingsWindow(_settings)
        {
            Owner = Application.Current.MainWindow,
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        // Reglages relus depuis le disque plutot que repris du modele de la
        // fenetre : le fichier est la source de verite, et cela couvre le cas
        // ou il aurait ete modifie a la main entre-temps.
        _settings = ClientSettings.Load();

        _client.Dispose();
        _client = new ControlApiClient(_settings);

        Dashboard = new DashboardViewModel(_client);
        OnPropertyChanged(nameof(Dashboard));

        // Le theme vient d'etre applique par l'ecran de reglages : le repasser
        // par la propriete relancerait Apply et une ecriture de fichier
        // inutile, d'ou le garde-fou.
        var applied = ThemeService.IsDark;

        if (IsDarkTheme != applied)
        {
            _suppressThemePersistence = true;
            IsDarkTheme = applied;
            _suppressThemePersistence = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (_suppressThemePersistence)
        {
            return;
        }

        var theme = value ? AppTheme.Sombre : AppTheme.Clair;
        ThemeService.Apply(theme);

        // Le choix est persiste immediatement : un exploitant qui bascule en
        // sombre ne doit pas retrouver du blanc au prochain lancement.
        _settings = _settings with { Theme = theme };
        _settings.Save();
    }

    private async Task OpenEditorAsync(string? name, bool isTemplate)
    {
        var window = new ConfigurationEditorWindow(_client, name, isTemplate)
        {
            Owner = Application.Current.MainWindow,
        };

        // Le rafraichissement periodique est suspendu pendant l'edition : il
        // remplacerait la selection sous les doigts au retour.
        IsBusy = true;

        try
        {
            if (window.ShowDialog() == true)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshLoopAsync()
    {
        await RefreshAsync().ConfigureAwait(true);

        try
        {
            while (await _refreshTimer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(true))
            {
                // Pas de rafraichissement pendant une commande longue : un test
                // FTP de deux minutes ne doit pas etre interrompu par le
                // minuteur qui remplace la selection sous les doigts.
                if (!IsBusy)
                {
                    await RefreshAsync().ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fermeture de la fenetre.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _refreshTimer.Dispose();
        _client.Dispose();
    }
}
