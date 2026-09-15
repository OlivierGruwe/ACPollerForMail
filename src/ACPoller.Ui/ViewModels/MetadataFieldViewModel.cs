using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ACPoller.Ui.ViewModels;

/// <summary>Une traduction de valeur d'un champ personnalise.</summary>
public sealed partial class ValueMapping : ObservableObject
{
    [ObservableProperty]
    private string _from = string.Empty;

    [ObservableProperty]
    private string _to = string.Empty;

    /// <summary>Construit une traduction vide.</summary>
    public ValueMapping()
    {
    }

    /// <summary>Construit une traduction renseignee.</summary>
    /// <param name="from">Valeur d'origine.</param>
    /// <param name="to">Valeur de remplacement.</param>
    public ValueMapping(string from, string to)
    {
        _from = from;
        _to = to;
    }
}

/// <summary>
/// Un champ ajoute au fichier d'information, en cours d'edition.
/// </summary>
/// <remarks>
/// L'ordre des champs dans la liste est celui des colonnes du CSV : le
/// deplacer change le format de sortie, ce que l'ecran doit rendre evident.
/// </remarks>
public sealed partial class MetadataFieldViewModel : ObservableObject
{
    /// <summary>Origines de valeur proposees.</summary>
    public static IReadOnlyList<string> Sources { get; } =
        ["Fixed", "Token", "Header", "Property", "Environment"];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _source = "Token";

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _format = string.Empty;

    [ObservableProperty]
    private string _default = string.Empty;

    [ObservableProperty]
    private int _fixedLength;

    [ObservableProperty]
    private bool _padLeft;

    /// <summary>Traductions de valeur appliquees apres resolution.</summary>
    public ObservableCollection<ValueMapping> Values { get; } = [];

    /// <summary>Aide contextuelle selon l'origine choisie.</summary>
    public string ValueHint => Source switch
    {
        "Fixed" => "Valeur litterale, identique pour tous les messages.",
        "Token" => "Gabarit a jetons : {date:yyyy-MM-dd}, {received}, {subject}, {from}, {mailbox}, {config}.",
        "Header" => "Nom de l'en-tete MIME, insensible a la casse. Exemple : X-Priority.",
        "Property" => "Cle deposee par un traitement metier. Exemple : Supplier.",
        "Environment" => "Nom d'une variable d'environnement du serveur. Exemple : COMPUTERNAME.",
        _ => string.Empty,
    };

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(ValueHint));

    /// <summary>Construit un champ depuis le JSON.</summary>
    /// <param name="node">Noeud du champ.</param>
    /// <returns>Le champ editable.</returns>
    public static MetadataFieldViewModel FromJson(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var field = new MetadataFieldViewModel
        {
            Name = node["Name"]?.GetValue<string>() ?? string.Empty,
            Source = node["Source"]?.GetValue<string>() ?? "Token",
            Value = node["Value"]?.GetValue<string>() ?? string.Empty,
            Format = node["Format"]?.GetValue<string>() ?? string.Empty,
            Default = node["Default"]?.GetValue<string>() ?? string.Empty,
            FixedLength = node["FixedLength"]?.GetValue<int>() ?? 0,
            PadLeft = node["PadLeft"]?.GetValue<bool>() ?? false,
        };

        if (node["Values"] is JsonObject values)
        {
            foreach (var (from, to) in values)
            {
                field.Values.Add(new ValueMapping(from, to?.ToString() ?? string.Empty));
            }
        }

        return field;
    }

    /// <summary>Produit le JSON du champ.</summary>
    /// <returns>Le noeud a placer dans Output:Metadata:Fields.</returns>
    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["Name"] = Name.Trim(),
            ["Source"] = Source,
            ["Value"] = Value,
        };

        // Les proprietes vides ne sont PAS ecrites : un fichier de
        // configuration lisible vaut mieux qu'un fichier exhaustif, et le
        // service applique ses propres defauts.
        if (!string.IsNullOrWhiteSpace(Format))
        {
            node["Format"] = Format.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Default))
        {
            node["Default"] = Default;
        }

        if (FixedLength > 0)
        {
            node["FixedLength"] = FixedLength;
            node["PadLeft"] = PadLeft;
        }

        var values = Values.Where(v => !string.IsNullOrWhiteSpace(v.From)).ToArray();

        if (values.Length > 0)
        {
            var mapping = new JsonObject();

            foreach (var value in values)
            {
                mapping[value.From] = value.To;
            }

            node["Values"] = mapping;
        }

        return node;
    }
}
