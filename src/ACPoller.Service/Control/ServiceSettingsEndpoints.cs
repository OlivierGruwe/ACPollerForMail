using System.Text.Json.Nodes;
using ACPoller.Core.Configuration;
using ACPoller.Service.Security;

namespace ACPoller.Service.Control;

/// <summary>
/// Points d'entree des reglages de service.
/// </summary>
/// <remarks>
/// Distincts des configurations de boite pour une raison de fond : ceux-ci ne
/// sont PAS rechargeables a chaud. Les melanger dans le meme ecran laisserait
/// croire qu'ils se comportent pareil, alors qu'une modification de
/// concurrence globale dort jusqu'au prochain redemarrage du service.
///
/// Le redemarrage n'est pas declenche ici : le service ne peut pas s'arreter
/// sur ordre de son propre client HTTP sans laisser l'appelant sans reponse,
/// incapable de savoir si l'arret a reussi. C'est le gestionnaire de services
/// qui s'en charge, et l'interface le dit.
/// </remarks>
public static class ServiceSettingsEndpoints
{
    /// <summary>Reglages secrets, masques en lecture et conserves si vides.</summary>
    private static readonly string[] SecretPaths =
    [
        "ControlApi:Token",
        "Metrics:SqlServer:ConnectionString",
        "Notifications:Smtp:Password",
    ];

    /// <summary>Declare les points d'entree.</summary>
    /// <param name="group">Groupe de routes deja protege par le filtre de jeton.</param>
    /// <returns>Le groupe, pour le chainage.</returns>
    public static RouteGroupBuilder MapServiceSettingsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", UpdateSettings);

        return group;
    }

    /// <summary>
    /// Renvoie les reglages de service, secrets masques.
    /// </summary>
    /// <remarks>
    /// Les sections Configurations et Templates sont retirees : elles ont leur
    /// propre ecran, et les transporter ici ferait circuler des dizaines de
    /// kilo-octets a chaque ouverture pour rien.
    /// </remarks>
    private static IResult GetSettings()
    {
        var root = ReadRoot();

        if (root is null)
        {
            return Results.Problem("Fichier de configuration illisible.");
        }

        var copy = root.DeepClone().AsObject();

        if (copy["Poller"] is JsonObject poller)
        {
            poller.Remove("Configurations");
            poller.Remove("Templates");
        }

        foreach (var path in SecretPaths)
        {
            MaskSecret(copy, path);
        }

        return Results.Ok(new
        {
            version = ConfigurationWriter.ComputeVersion(),
            settings = copy,
        });
    }

    private static async Task<IResult> UpdateSettings(
        ConfigurationPayload payload,
        ConfigurationWriter writer,
        SettingsProtectionService protection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var result = writer.UpdateServiceSettings(payload.Configuration, payload.Version);

        if (!result.Success)
        {
            return Results.Conflict(result);
        }

        await protection.ProtectAsync(ConfigurationWriter.SettingsPath, cancellationToken)
            .ConfigureAwait(false);

        // Aucun worker n'est redemarre : ces reglages ne sont pas rechargeables,
        // et un redemarrage de worker donnerait l'illusion qu'ils ont pris
        // effet. Le RestartTracker a marque ce qui attend, l'interface
        // l'affiche.
        return Results.Ok(result with { Version = ConfigurationWriter.ComputeVersion() });
    }

    private static JsonNode? ReadRoot()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigurationWriter.SettingsPath));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remplace un secret par une chaine vide, en suivant un chemin a deux
    /// points. Vide et non un masque litteral : le writer interprete un champ
    /// vide comme "inchange", un masque serait enregistre tel quel.
    /// </summary>
    private static void MaskSecret(JsonObject root, string path)
    {
        var segments = path.Split(':');
        JsonObject? current = root;

        for (var i = 0; i < segments.Length - 1 && current is not null; i++)
        {
            current = current[segments[i]] as JsonObject;
        }

        if (current?[segments[^1]] is not null)
        {
            current[segments[^1]] = string.Empty;
        }
    }
}
