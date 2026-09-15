using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACPoller.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ACPoller.Ui.ViewModels;

/// <summary>
/// Edition du JSON d'une configuration.
/// </summary>
/// <remarks>
/// L'edition porte sur le JSON reel et non sur un modele reconstitue. Deux
/// raisons : le fichier peut contenir des reglages qu'une version plus ancienne
/// de l'UI ne connait pas, et les perdre a chaque enregistrement serait
/// inacceptable ; et les reglages d'une cible d'export sont un dictionnaire
/// libre, qu'aucun formulaire ne peut couvrir a l'avance.
///
/// Un formulaire pour les champs courants pourra se poser par-dessus plus tard :
/// il ecrira dans le meme arbre JSON, sans remplacer cet ecran.
/// </remarks>
public sealed partial class ConfigurationEditorViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions FormatOptions = new() { WriteIndented = true };

    private readonly ControlApiClient _client;
    private readonly bool _isNew;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _json = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private bool _isBusy;

    private string? _version;

    /// <summary>Construit l'editeur pour une configuration existante ou nouvelle.</summary>
    /// <param name="client">Client de l'API de pilotage.</param>
    /// <param name="name">Nom de la configuration, vide pour une creation.</param>
    public ConfigurationEditorViewModel(ControlApiClient client, string? name)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _isNew = string.IsNullOrWhiteSpace(name);
        _name = name ?? "NOUVELLE-CONFIGURATION";

        if (_isNew)
        {
            Json = Gabarit;
            StatusMessage = "Nouvelle configuration. Renseigner la source et au moins une cible.";
        }
    }

    /// <summary>Anomalies renvoyees par le service lors du dernier refus.</summary>
    public ObservableCollection<IssueDto> Issues { get; } = [];

    /// <summary>Vrai si les modifications ont ete enregistrees.</summary>
    public bool Saved { get; private set; }

    /// <summary>Charge le JSON depuis le service.</summary>
    /// <returns>Une tache achevee apres chargement.</returns>
    public async Task LoadAsync()
    {
        if (_isNew)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var raw = await _client.GetRawAsync(Name, CancellationToken.None).ConfigureAwait(true);

            if (raw?.Configuration is null)
            {
                SetStatus("Configuration introuvable.", isError: true);
                return;
            }

            // L'empreinte est conservee : elle sera renvoyee a l'enregistrement
            // pour detecter une modification concurrente du fichier.
            _version = raw.Version;
            Json = raw.Configuration.ToJsonString(FormatOptions);

            SetStatus("Les champs de secret sont vides : les laisser ainsi conserve la valeur actuelle.", false);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Chargement impossible : {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Verifie que le JSON saisi est valide, sans rien envoyer.</summary>
    [RelayCommand]
    public void Validate()
    {
        if (TryParse(out _))
        {
            SetStatus("JSON valide.", isError: false);
        }
    }

    /// <summary>Reformate le JSON avec une indentation lisible.</summary>
    [RelayCommand]
    public void Format()
    {
        if (TryParse(out var node) && node is not null)
        {
            Json = node.ToJsonString(FormatOptions);
            SetStatus("JSON reformate.", isError: false);
        }
    }

    /// <summary>Enregistre la configuration sur le service.</summary>
    /// <returns>Une tache achevee apres enregistrement.</returns>
    [RelayCommand]
    public async Task SaveAsync()
    {
        Issues.Clear();

        // Validation locale d'abord : inutile de faire un aller-retour reseau
        // pour une virgule en trop, et le message d'erreur du parseur local
        // designe la position exacte.
        if (!TryParse(out var node) || node is not JsonObject json)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _client
                .UpsertAsync(Name.Trim(), json, _version, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var issue in result.Issues)
            {
                Issues.Add(issue);
            }

            if (!result.Success)
            {
                SetStatus(result.Message, isError: true);
                return;
            }

            _version = result.Version;
            Saved = true;

            SetStatus(result.Message, isError: false);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Enregistrement impossible : {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryParse(out JsonNode? node)
    {
        node = null;

        try
        {
            node = JsonNode.Parse(Json);

            if (node is not JsonObject)
            {
                SetStatus("Le contenu doit etre un objet JSON.", isError: true);
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            // Le message du parseur porte la ligne et la position : c'est
            // l'information utile, la reformuler la ferait perdre.
            SetStatus($"JSON invalide : {ex.Message}", isError: true);
            return false;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    /// <summary>
    /// Gabarit d'une nouvelle configuration : le minimum viable, avec les
    /// valeurs qui posent probleme quand on les oublie.
    /// </summary>
    private const string Gabarit = """
        {
          "Name": "NOUVELLE-CONFIGURATION",
          "Enabled": false,
          "Source": {
            "Protocol": "graph",
            "Mailbox": "boite@client.fr",
            "TenantId": "",
            "ClientId": "",
            "ClientSecret": "",
            "Folder": { "WellKnown": "Inbox" },
            "Disposition": "MarkAsRead"
          },
          "Query": {
            "UnreadOnly": true,
            "MaxCount": 200,
            "NotBefore": null
          },
          "Schedule": {
            "IntervalSeconds": 120,
            "StartupJitterSeconds": 30
          },
          "Conversion": {
            "Timeout": "00:02:00",
            "OnFailure": "Substitute",
            "IncludeBody": true
          },
          "Output": {
            "PdfMode": "Merged",
            "Metadata": { "Format": "json" },
            "Naming": "{received:yyyyMMdd_HHmmss}_{from}_{guid}",
            "Targets": [
              {
                "Type": "fs",
                "Name": "ged",
                "Settings": {
                  "Path": "\\\\serveur\\partage\\in",
                  "CreateDirectory": "true"
                }
              }
            ],
            "Policy": { "AtomicWrite": true, "AllOrNothing": true }
          },
          "Processors": []
        }
        """;
}
