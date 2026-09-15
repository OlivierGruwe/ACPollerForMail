using System.Windows;
using ACPoller.Ui.Services;
using ACPoller.Ui.ViewModels;
using MahApps.Metro.Controls;

namespace ACPoller.Ui.Views;

/// <summary>Fenetre de reglages de connexion au service.</summary>
public partial class SettingsWindow : MetroWindow
{
    private readonly SettingsViewModel _model;

    /// <summary>Construit la fenetre a partir des reglages courants.</summary>
    /// <param name="settings">Reglages a editer.</param>
    public SettingsWindow(ClientSettings settings)
    {
        InitializeComponent();

        _model = new SettingsViewModel(settings);
        DataContext = _model;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _model.SaveCommand.Execute(null);

        // DialogResult a true : la fenetre principale sait qu'elle doit
        // reconstruire son client HTTP avec la nouvelle adresse.
        DialogResult = true;
    }
}
