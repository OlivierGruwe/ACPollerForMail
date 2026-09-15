using System.IO;
using System.Net;
using System.Net.Http;
using ACPoller.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACPoller.Ui.ViewModels;

/// <summary>
/// Reglages de connexion au service, testables avant enregistrement.
/// </summary>
/// <remarks>
/// Le test avant enregistrement est le point central de cet ecran. Sans lui,
/// une adresse ou un jeton errone se manifeste par un ecran vide et un message
/// generique, et l'exploitant cherche une panne de service la ou il y a une
/// faute de frappe. Ici, il sait avant de valider.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ClientSettings _original;

    /// <summary>Construit le modele a partir des reglages enregistres.</summary>
    /// <param name="settings">Reglages courants.</param>
    public SettingsViewModel(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _original = settings;
        _baseAddress = settings.BaseAddress;
        _token = settings.Token;
        _theme = settings.Theme;
        _language = settings.Language;
    }

    [ObservableProperty]
    private string _baseAddress;

    [ObservableProperty]
    private string _token;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private bool _isTesting;

    /// <summary>Emplacement des reglages de l'utilisateur.</summary>
    public static string SettingsLocation => ClientSettings.Location;

    /// <summary>Langues proposees.</summary>
    public static IReadOnlyList<AppLanguage> Languages { get; } =
        [AppLanguage.Systeme, AppLanguage.Francais, AppLanguage.English];

    [ObservableProperty]
    private AppLanguage _language;

    /// <summary>
    /// Emplacement des reglages deposes par l'installeur, ou null s'il n'y en
    /// a pas. Absent sur un poste ou seule l'interface est installee.
    /// </summary>
    public static string? DefaultsLocation =>
        File.Exists(ClientSettings.DefaultsLocation) ? ClientSettings.DefaultsLocation : null;

    /// <summary>Vrai si des reglages ont ete deposes a l'installation.</summary>
    public static bool HasDefaults => DefaultsLocation is not null;

    /// <summary>Themes proposes dans la liste.</summary>
    public static IReadOnlyList<AppTheme> Themes { get; } =
        [AppTheme.Systeme, AppTheme.Clair, AppTheme.Sombre];

    /// <summary>Vrai si les reglages ont ete enregistres et doivent etre repris.</summary>
    public bool Saved { get; private set; }

    /// <summary>Teste la connexion avec les valeurs saisies, sans les enregistrer.</summary>
    /// <returns>Une tache achevee a la fin du test.</returns>
    [RelayCommand]
    public async Task TestAsync()
    {
        IsTesting = true;
        TestResult = "Test en cours...";

        try
        {
            // Client construit sur les valeurs EN COURS DE SAISIE, pas sur les
            // reglages enregistres : c'est tout l'interet du bouton.
            using var client = new ControlApiClient(new ClientSettings
            {
                BaseAddress = BaseAddress.Trim(),
                Token = Token.Trim(),
            });

            var status = await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);

            TestSucceeded = true;
            TestResult = status is null
                ? "Service joignable, mais reponse vide."
                : $"Service {status.Version} joignable, {status.Workers.Count} worker(s).";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Message explicite : un 401 renvoie vers le jeton, pas vers le
            // service. C'est la confusion qui coute le plus de temps.
            TestSucceeded = false;
            TestResult = "Jeton refuse. Verifier la valeur de ControlApi:Token dans "
                + "la configuration du service.";
        }
        catch (HttpRequestException ex)
        {
            TestSucceeded = false;
            TestResult = $"Service injoignable : {ex.Message}";
        }
        catch (Exception ex) when (ex is TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            TestSucceeded = false;
            TestResult = $"Adresse invalide ou delai depasse : {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Enregistre les reglages et applique le theme.</summary>
    [RelayCommand]
    public void Save()
    {
        var settings = _original with
        {
            BaseAddress = BaseAddress.Trim(),

            // Trim systematique : un espace copie avec le jeton donne un 401
            // parfaitement incomprehensible, et c'est arrive.
            Token = Token.Trim(),
            Theme = Theme,
            Language = Language,    
        };

        settings.Save();
        LocalizationService.Apply(Language);
        ThemeService.Apply(Theme);

        Saved = true;
    }
}
