using MahApps.Metro.Controls;

namespace ACPoller.Ui.Views;

/// <summary>
/// Aide de reference : jetons, statuts, champs personnalises, diagnostic.
/// </summary>
/// <remarks>
/// Contenu fige dans le XAML plutot que charge depuis un fichier : une aide
/// qui peut manquer a l'installation n'est pas une aide. Elle est livree avec
/// l'assemblage et ne peut pas se desynchroniser du produit.
/// </remarks>
public partial class HelpWindow : MetroWindow
{
    /// <summary>Construit la fenetre d'aide.</summary>
    public HelpWindow() => InitializeComponent();
}
