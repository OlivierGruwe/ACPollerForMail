using System.Windows;

namespace ACPoller.Ui.Services;

/// <summary>Langues proposees par l'interface.</summary>
public enum AppLanguage
{
    /// <summary>Suit la langue de Windows.</summary>
    Systeme = 0,

    /// <summary>Francais.</summary>
    Francais = 1,

    /// <summary>Anglais.</summary>
    English = 2,
}

/// <summary>
/// Applique la langue de l'interface.
/// </summary>
/// <remarks>
/// Dictionnaires de ressources XAML plutot que fichiers .resx, pour une raison
/// precise : les chaines sont consommees par DynamicResource, qui se resout a
/// nouveau quand le dictionnaire change. Le basculement est donc IMMEDIAT,
/// sans redemarrer l'interface ni rouvrir les fenetres.
///
/// Un .resx imposerait un redemarrage, ou un mecanisme de notification sur
/// chaque chaine, ce qui reviendrait a reecrire ce que WPF fait deja.
/// </remarks>
public static class LocalizationService
{
    private const string Prefix = "pack://application:,,,/Resources/Strings.";

    /// <summary>Langue actuellement appliquee.</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.Systeme;

    /// <summary>Applique la langue demandee.</summary>
    /// <param name="language">Langue voulue.</param>
    public static void Apply(AppLanguage language)
    {
        var application = System.Windows.Application.Current;

        if (application is null)
        {
            return;
        }

        var code = Resolve(language);
        var uri = new Uri($"{Prefix}{code}.xaml", UriKind.Absolute);

        var dictionary = new ResourceDictionary { Source = uri };

        // L'ancien dictionnaire de chaines est RETIRE avant l'ajout du nouveau.
        // Les empiler ferait que la premiere langue chargee gagnerait, WPF
        // resolvant dans l'ordre inverse d'insertion mais conservant la
        // premiere occurrence trouvee pour un DynamicResource deja resolu.
        var existing = application.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.StartsWith(Prefix, StringComparison.Ordinal) == true);

        if (existing is not null)
        {
            application.Resources.MergedDictionaries.Remove(existing);
        }

        application.Resources.MergedDictionaries.Add(dictionary);

        Current = language;
    }

    /// <summary>Code de langue effectivement applique.</summary>
    /// <param name="language">Langue voulue.</param>
    /// <returns>"fr" ou "en".</returns>
    private static string Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Francais => "fr",
        AppLanguage.English => "en",

        // Suivre Windows est le defaut : un exploitant anglophone sur un
        // serveur anglais ne devrait pas avoir a configurer quoi que ce soit.
        // Toute langue autre que le francais retombe sur l'anglais, qui est la
        // langue de repli habituelle des outils d'exploitation.
        _ => System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName == "fr" ? "fr" : "en",
    };
}
