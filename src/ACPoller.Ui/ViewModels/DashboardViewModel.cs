using System.Collections.ObjectModel;
using System.Net.Http;
using ACPoller.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACPoller.Ui.ViewModels;

/// <summary>Une barre de l'histogramme horaire.</summary>
/// <remarks>
/// La hauteur est calculee ici et non dans le XAML : WPF ne sait pas rapporter
/// une valeur a un maximum en liaison simple, et un convertisseur multiple
/// pour cela serait plus lourd que ces trois lignes.
/// </remarks>
public sealed record HourlyBar
{
    /// <summary>Heure representee, en heure locale.</summary>
    public required DateTimeOffset Hour { get; init; }

    /// <summary>Valeur brute.</summary>
    public required double Value { get; init; }

    /// <summary>Hauteur en pixels, rapportee au maximum de la serie.</summary>
    public required double Height { get; init; }

    /// <summary>Libelle affiche sous la barre, une heure sur trois.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Infobulle : heure et valeur exacte.</summary>
    public string ToolTip => $"{Hour:HH}h : {Value:N0} message(s)";
}

/// <summary>
/// Tableau de bord d'exploitation.
/// </summary>
/// <remarks>
/// Chaque indicateur repond a une question precise : est-ce que ca tourne, y
/// a-t-il des messages lents, suis-je pres du quota Graph, combien de temps
/// avant que le disque sature, quelle boite pose probleme. Un chiffre qui ne
/// repond a aucune de ces questions n'a pas sa place ici.
/// </remarks>
public sealed partial class DashboardViewModel(ControlApiClient client) : ObservableObject
{
    private const double ChartHeight = 120;

    [ObservableProperty]
    private DashboardDto? _data;

    [ObservableProperty]
    private string _windowLabel = string.Empty;

    /// <summary>Histogramme du volume traite par heure.</summary>
    public ObservableCollection<HourlyBar> HourlyBars { get; } = [];

    /// <summary>Ventilation par configuration, la plus en echec en tete.</summary>
    public ObservableCollection<ConfigurationMetricsDto> ByConfiguration { get; } = [];

    /// <summary>Vrai quand le taux de succes est sous le seuil d'alerte.</summary>
    public bool HasFailures => Data is { Failed: > 0 };

    /// <summary>Vrai quand des limitations Graph ont ete rencontrees.</summary>
    public bool HasThrottling => Data is { ThrottleEvents: > 0 };

    /// <summary>
    /// Vrai quand l'espace disque passe sous le seuil. Le service s'arretera a
    /// saturation, autant le voir venir.
    /// </summary>
    public bool HasDiskWarning => Data?.FreeDiskPercent is { } percent && percent < 15;

    /// <summary>Recharge le tableau de bord.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Une tache achevee apres rafraichissement.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = await client.GetDashboardAsync(cancellationToken).ConfigureAwait(true);

            if (data is null)
            {
                return;
            }

            Data = data;

            var since = data.SinceUtc.ToLocalTime();
            WindowLabel = $"Depuis le {since:dd/MM HH:mm}";

            BuildChart(data.HourlyExported);

            ByConfiguration.Clear();

            foreach (var configuration in data.ByConfiguration)
            {
                ByConfiguration.Add(configuration);
            }

            OnPropertyChanged(nameof(HasFailures));
            OnPropertyChanged(nameof(HasThrottling));
            OnPropertyChanged(nameof(HasDiskWarning));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Le tableau de bord n'est pas critique : son indisponibilite ne
            // doit pas masquer l'ecran de supervision, qui porte deja l'etat
            // de la connexion.
        }
    }

    /// <summary>Remet les compteurs a zero.</summary>
    /// <returns>Une tache achevee apres remise a zero.</returns>
    [RelayCommand]
    public async Task ResetAsync()
    {
        try
        {
            await client.ResetMetricsAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Sans effet visible : le prochain rafraichissement corrigera.
        }
    }

    private void BuildChart(List<HourlyPointDto> points)
    {
        HourlyBars.Clear();

        if (points.Count == 0)
        {
            return;
        }

        var max = points.Max(p => p.Value);

        // Maximum nul : toutes les barres seraient a zero et l'echelle
        // diviserait par zero. Un plancher a 1 evite les deux.
        max = max <= 0 ? 1 : max;

        var index = 0;

        foreach (var point in points)
        {
            var local = point.HourUtc.ToLocalTime();

            HourlyBars.Add(new HourlyBar
            {
                Hour = local,
                Value = point.Value,

                // Hauteur minimale de deux pixels pour une valeur non nulle :
                // une heure a un seul message doit rester visible, sinon on
                // croit a un trou dans le flux.
                Height = point.Value <= 0 ? 0 : Math.Max(2, point.Value / max * ChartHeight),

                // Une etiquette sur trois : au-dela, elles se chevauchent sur
                // vingt-quatre barres.
                Label = index++ % 3 == 0 ? $"{local:HH}h" : string.Empty,
            });
        }
    }
}
