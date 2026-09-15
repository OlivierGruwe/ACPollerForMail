using System.Windows;
using System.Windows.Media;
using ControlzEx.Theming;
// Windows Forms, present pour NotifyIcon, rend Color ambigu dans tout le
// projet : System.Drawing.Color contre System.Windows.Media.Color.
using Color = System.Windows.Media.Color;

namespace ACPoller.Ui.Services;

/// <summary>Thème d'affichage.</summary>
public enum AppTheme
{
    /// <summary>Suit le réglage de Windows.</summary>
    Systeme = 0,

    /// <summary>Clair.</summary>
    Clair = 1,

    /// <summary>Sombre.</summary>
    Sombre = 2,
}

/// <summary>
/// Applique le thème MahApps avec la couleur d'accentuation Arondor.
/// </summary>
/// <remarks>
/// Le thème est généré à l'exécution plutôt que choisi parmi les thèmes
/// prédéfinis : aucun accent livré avec MahApps ne correspond au bleu de la
/// charte, et un écran d'exploitation qui porte le logo d'une marque doit en
/// porter aussi les couleurs, sinon l'ensemble a l'air d'un assemblage.
/// </remarks>
public static class ThemeService
{
    /// <summary>Bleu Arondor, repris du logo.</summary>
    public static readonly Color AccentColor = Color.FromRgb(0x00, 0xA6, 0xDE);

    /// <summary>Gris ardoise Arondor, pour les éléments secondaires.</summary>
    public static readonly Color SecondaryColor = Color.FromRgb(0x5B, 0x66, 0x7A);

    /// <summary>Applique le thème demandé à l'application entière.</summary>
    /// <param name="theme">Thème voulu.</param>
    public static void Apply(AppTheme theme)
    {
        var application = System.Windows.Application.Current;

        if (application is null)
        {
            return;
        }

        var baseScheme = theme switch
        {
            AppTheme.Clair => ThemeManager.BaseColorLight,
            AppTheme.Sombre => ThemeManager.BaseColorDark,

            // Suivre Windows est le défaut : sur un poste d'exploitation
            // configuré en sombre, une fenêtre blanche qui s'ouvre en pleine
            // nuit de garde est une agression.
            _ => ThemeManager.Current.DetectTheme()?.BaseColorScheme
                 ?? ThemeManager.BaseColorLight,
        };

        var generated = RuntimeThemeGenerator.Current.GenerateRuntimeTheme(baseScheme, AccentColor);

        if (generated is not null)
        {
            ThemeManager.Current.ChangeTheme(application, generated);
        }
    }

    /// <summary>Bascule entre clair et sombre.</summary>
    /// <param name="current">Thème courant.</param>
    /// <returns>Le thème appliqué.</returns>
    public static AppTheme Toggle(AppTheme current)
    {
        var next = current == AppTheme.Sombre ? AppTheme.Clair : AppTheme.Sombre;
        Apply(next);
        return next;
    }

    /// <summary>Vrai si le thème actuellement appliqué est sombre.</summary>
    public static bool IsDark =>
        ThemeManager.Current.DetectTheme(System.Windows.Application.Current)?.BaseColorScheme
            == ThemeManager.BaseColorDark;
}
