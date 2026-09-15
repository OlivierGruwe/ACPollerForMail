using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ACPoller.Ui.ViewModels;

/// <summary>Une entree du dictionnaire de reglages d'une cible.</summary>
public sealed partial class SettingEntry : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Construit une entree vide.</summary>
    public SettingEntry()
    {
    }

    /// <summary>Construit une entree renseignee.</summary>
    /// <param name="key">Nom du reglage.</param>
    /// <param name="value">Valeur.</param>
    public SettingEntry(string key, string value)
    {
        _key = key;
        _value = value;
    }
}

/// <summary>
/// Une cible d'export en cours d'edition.
/// </summary>
/// <remarks>
/// Les proprietes dediees et le tableau de reglages pointent vers LE MEME
/// dictionnaire : modifier l'un met l'autre a jour, il n'y a pas deux etats a
/// reconcilier. Le tableau reste accessible pour les reglages qu'aucun champ
/// dedie ne couvre, notamment ceux d'une DLL d'export cliente.
/// </remarks>
public sealed partial class TargetViewModel : ObservableObject
{
    /// <summary>Types de transport proposes.</summary>
    public static IReadOnlyList<string> Types { get; } = ["fs", "ftp", "s3", "plugin"];

    /// <summary>Modes de chiffrement FTP.</summary>
    public static IReadOnlyList<string> EncryptionModes { get; } = ["explicit", "implicit", "none"];

    [ObservableProperty]
    private string _type = "fs";

    [ObservableProperty]
    private string _name = "ged";

    /// <summary>Reglages libres, y compris ceux non couverts par les champs dedies.</summary>
    public ObservableCollection<SettingEntry> Settings { get; } = [];

    /// <summary>Vrai pour un depot sur systeme de fichiers.</summary>
    public bool IsFileSystem => Type == "fs";

    /// <summary>Vrai pour un transfert FTP ou FTPS.</summary>
    public bool IsFtp => Type == "ftp";

    /// <summary>Vrai pour un depot S3 ou compatible.</summary>
    public bool IsS3 => Type == "s3";

    /// <summary>Vrai pour une DLL d'export cliente.</summary>
    public bool IsPlugin => Type == "plugin";

    partial void OnTypeChanged(string value)
    {
        // Sans ces notifications, changer le type ne modifie rien a l'ecran :
        // les panneaux conditionnels ne sont jamais reevalues.
        OnPropertyChanged(nameof(IsFileSystem));
        OnPropertyChanged(nameof(IsFtp));
        OnPropertyChanged(nameof(IsS3));
        OnPropertyChanged(nameof(IsPlugin));
    }

    /// <summary>Repertoire de depot, local ou UNC.</summary>
    public string Path
    {
        get => Get("Path");
        set => Set("Path", value);
    }

    /// <summary>Creer le repertoire s'il n'existe pas.</summary>
    public bool CreateDirectory
    {
        get => GetBool("CreateDirectory", defaultValue: true);
        set => Set("CreateDirectory", value ? "true" : "false");
    }

    /// <summary>Hote FTP.</summary>
    public string Host
    {
        get => Get("Host");
        set => Set("Host", value);
    }

    /// <summary>Port FTP. 21 en explicite, 990 en implicite.</summary>
    public string Port
    {
        get => Get("Port");
        set => Set("Port", value);
    }

    /// <summary>Utilisateur FTP.</summary>
    public string UserName
    {
        get => Get("UserName");
        set => Set("UserName", value);
    }

    /// <summary>Mot de passe FTP. Vide signifie inchange.</summary>
    public string Password
    {
        get => Get("Password");
        set => Set("Password", value);
    }

    /// <summary>Repertoire distant de depot.</summary>
    public string RemoteDirectory
    {
        get => Get("RemoteDirectory");
        set => Set("RemoteDirectory", value);
    }

    /// <summary>Mode de chiffrement : explicit, implicit ou none.</summary>
    public string EncryptionMode
    {
        get => Get("EncryptionMode") is { Length: > 0 } mode ? mode : "explicit";
        set => Set("EncryptionMode", value);
    }

    /// <summary>
    /// Accepter tout certificat serveur. A n'activer qu'en connaissance de
    /// cause : cela supprime toute protection contre l'interception.
    /// </summary>
    public bool AcceptAnyCertificate
    {
        get => GetBool("AcceptAnyCertificate", defaultValue: false);
        set => Set("AcceptAnyCertificate", value ? "true" : "false");
    }

    /// <summary>Bucket de destination.</summary>
    public string Bucket
    {
        get => Get("Bucket");
        set => Set("Bucket", value);
    }

    /// <summary>Prefixe des cles.</summary>
    public string Prefix
    {
        get => Get("Prefix");
        set => Set("Prefix", value);
    }

    /// <summary>Region AWS.</summary>
    public string Region
    {
        get => Get("Region");
        set => Set("Region", value);
    }

    /// <summary>Point d'acces personnalise, pour MinIO, Garage ou Ceph.</summary>
    public string ServiceUrl
    {
        get => Get("ServiceUrl");
        set => Set("ServiceUrl", value);
    }

    /// <summary>Cle d'acces. Vide pour utiliser la chaine de resolution AWS.</summary>
    public string AccessKey
    {
        get => Get("AccessKey");
        set => Set("AccessKey", value);
    }

    /// <summary>Cle secrete. Vide signifie inchange.</summary>
    public string SecretKey
    {
        get => Get("SecretKey");
        set => Set("SecretKey", value);
    }

    /// <summary>Chiffrement au repos : AES256, aws:kms ou vide.</summary>
    public string ServerSideEncryption
    {
        get => Get("ServerSideEncryption");
        set => Set("ServerSideEncryption", value);
    }

    /// <summary>Nom de l'assemblage d'export client.</summary>
    public string Assembly
    {
        get => Get("Assembly");
        set => Set("Assembly", value);
    }

    /// <summary>Construit une cible depuis le JSON.</summary>
    /// <param name="node">Noeud de la cible.</param>
    /// <returns>La cible editable.</returns>
    public static TargetViewModel FromJson(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var target = new TargetViewModel
        {
            Type = node["Type"]?.GetValue<string>() ?? "fs",
            Name = node["Name"]?.GetValue<string>() ?? string.Empty,
        };

        if (node["Settings"] is JsonObject settings)
        {
            foreach (var (key, value) in settings)
            {
                target.Settings.Add(new SettingEntry(key, value?.ToString() ?? string.Empty));
            }
        }

        return target;
    }

    /// <summary>Produit le JSON de la cible.</summary>
    /// <returns>Le noeud a placer dans Output:Targets.</returns>
    public JsonObject ToJson()
    {
        var settings = new JsonObject();

        foreach (var entry in Settings.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
        {
            settings[entry.Key] = entry.Value;
        }

        return new JsonObject
        {
            ["Type"] = Type,
            ["Name"] = Name,
            ["Settings"] = settings,
        };
    }

    private string Get(string key) =>
        Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))?.Value
        ?? string.Empty;

    private bool GetBool(string key, bool defaultValue)
    {
        var value = Get(key);
        return string.IsNullOrEmpty(value) ? defaultValue : bool.TryParse(value, out var parsed) && parsed;
    }

    private void Set(string key, string value)
    {
        var entry = Settings.FirstOrDefault(s =>
            string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            Settings.Add(new SettingEntry(key, value));
        }
        else
        {
            entry.Value = value;
        }

        OnPropertyChanged(key);
    }
}
