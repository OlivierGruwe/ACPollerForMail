using CommunityToolkit.Mvvm.ComponentModel;

namespace ACPoller.Ui.ViewModels;

/// <summary>Un traitement metier, cochable dans la liste.</summary>
public sealed partial class ProcessorSelection : ObservableObject
{
    /// <summary>Nom declare par le processeur.</summary>
    public required string Name { get; init; }

    /// <summary>Etapes auxquelles il intervient, affichable.</summary>
    public required string Stages { get; init; }

    /// <summary>Ordre d'execution.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Vrai si le processeur n'est plus disponible mais reste declare dans la
    /// configuration. Le cas arrive quand un plugin est retire ou refuse.
    /// </summary>
    public bool IsMissing { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
