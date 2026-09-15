using System.Windows;
using ACPoller.Ui.Services;
using ACPoller.Ui.ViewModels;
using MahApps.Metro.Controls;

namespace ACPoller.Ui.Views;

/// <summary>Editeur de configuration par formulaire.</summary>
public partial class ConfigurationEditorWindow : MetroWindow
{
    private readonly ConfigurationFormViewModel _model;

    /// <summary>Construit l'editeur.</summary>
    /// <param name="client">Client de l'API de pilotage.</param>
    /// <param name="name">Nom, null pour une creation.</param>
    /// <param name="isTemplate">Vrai pour editer un gabarit.</param>
    public ConfigurationEditorWindow(ControlApiClient client, string? name, bool isTemplate = false)
    {
        InitializeComponent();

        _model = new ConfigurationFormViewModel(client, name, isTemplate);
        DataContext = _model;

        Title = isTemplate ? "Gabarit" : "Configuration";

        Loaded += async (_, _) => await _model.LoadAsync().ConfigureAwait(true);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await _model.SaveAsync().ConfigureAwait(true);

        // La fenetre ne se ferme QUE si l'enregistrement a reussi : un refus
        // pour conflit de version ou configuration invalide doit laisser
        // l'exploitant devant sa saisie, avec le message d'erreur.
        if (_model.Saved)
        {
            DialogResult = true;
        }
    }
}
