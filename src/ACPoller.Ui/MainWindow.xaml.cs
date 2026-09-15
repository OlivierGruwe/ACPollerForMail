using ACPoller.Ui.ViewModels;
using MahApps.Metro.Controls;
using Application = System.Windows.Application;
namespace ACPoller.Ui;

/// <summary>Fenetre de supervision du service.</summary>
public partial class MainWindow : MetroWindow, IDisposable
{
    private readonly TrayIcon _tray;
    private bool _disposed;

    /// <summary>Construit la fenetre.</summary>
    public MainWindow()
    {
        InitializeComponent();

        _tray = new TrayIcon(this);

        if (DataContext is MainViewModel model)
        {
            model.PropertyChanged += OnModelChanged;
        }

        Closed += (_, _) =>
        {
            Dispose();
            (DataContext as MainViewModel)?.Dispose();
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // L'icone de zone de notification survit a la fermeture si elle n'est
        // pas retiree explicitement : elle reste affichee jusqu'a ce que le
        // pointeur passe dessus.
        _tray.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel model)
        {
            return;
        }

        // Trois proprietes seulement declenchent une mise a jour : l'icone
        // n'a pas a se recalculer parce qu'une selection a change.
        if (e.PropertyName is nameof(MainViewModel.RestartRequired)
            or nameof(MainViewModel.IsConnected)
            or nameof(MainViewModel.ConnectionState))
        {
            _tray.Update(model.IsConnected, model.RestartRequired, model.ConnectionState);
        }
    }
}
