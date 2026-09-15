using System.Windows;
using ACPoller.Ui.Services;
using ACPoller.Ui.ViewModels;
using MahApps.Metro.Controls;

namespace ACPoller.Ui.Views;

/// <summary>Reglages de service, non rechargeables a chaud.</summary>
public partial class ServiceSettingsWindow : MetroWindow
{
    private readonly ServiceSettingsViewModel _model;

    /// <summary>Construit la fenetre.</summary>
    /// <param name="client">Client de l'API de pilotage.</param>
    public ServiceSettingsWindow(ControlApiClient client)
    {
        InitializeComponent();

        _model = new ServiceSettingsViewModel(client);
        DataContext = _model;

        Loaded += async (_, _) => await _model.LoadAsync().ConfigureAwait(true);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await _model.SaveAsync().ConfigureAwait(true);

        // La fenetre ne se ferme QUE si l'enregistrement a reussi : un refus
        // pour conflit de version ou reglage invalide doit laisser
        // l'exploitant devant sa saisie.
        if (_model.Saved)
        {
            DialogResult = true;
        }
    }
}
