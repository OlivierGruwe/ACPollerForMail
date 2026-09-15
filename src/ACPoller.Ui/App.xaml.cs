using System.Windows;
using ACPoller.Ui.Services;

namespace ACPoller.Ui;

/// <summary>
/// Point d'entree de l'application de supervision.
/// </summary>
/// <remarks>
/// L'UI ne lit et n'ecrit jamais directement les fichiers de configuration du
/// service : elle dialogue avec son API locale, qui reste la source de verite.
/// </remarks>
public partial class App : System.Windows.Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Le theme est applique AVANT l'ouverture de la fenetre : appliquer
        // apres produit un clignotement blanc sur un poste en mode sombre.
        var settings = ClientSettings.Load();
        // La langue est appliquee AVANT le theme et avant toute fenetre : un
        // dictionnaire charge apres coup laisserait les libelles vides le
        // temps du premier rendu.
        LocalizationService.Apply(settings.Language);


        ThemeService.Apply(settings.Theme);
    }
}
