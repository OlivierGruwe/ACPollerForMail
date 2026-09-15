using System.Text.Json.Nodes;
using ACPoller.Core.Configuration;
using ACPoller.Core.Workers;
using ACPoller.Service.Security;

namespace ACPoller.Service.Control;

/// <summary>
/// Points d'entree de modification de la configuration.
/// </summary>
/// <remarks>
/// Separes des points d'entree de lecture pour une raison simple : ce sont les
/// seuls qui peuvent casser un service en production. Les garder groupes rend
/// visible ce qui doit etre revu avec attention.
///
/// Sequence appliquee a chaque modification, dans cet ordre :
/// valider, ecrire, chiffrer les secrets, recharger, redemarrer le worker.
/// Inverser deux etapes suffit a produire un service qui tourne avec une
/// configuration qui n'est plus celle du fichier.
/// </remarks>
public static class ConfigurationEndpoints
{
    /// <summary>Declare les points d'entree d'ecriture.</summary>
    /// <param name="group">Groupe de routes deja protege par le filtre de jeton.</param>
    /// <returns>Le groupe, pour le chainage.</returns>
    public static RouteGroupBuilder MapConfigurationWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/configurations/{name}/raw", GetRaw);
        group.MapPut("/configurations/{name}", Upsert);
        group.MapDelete("/configurations/{name}", Delete);
        group.MapPost("/configurations/{name}/enabled", SetEnabled);

        return group;
    }

    /// <summary>
    /// Renvoie le JSON brut d'une configuration, secrets masques, avec
    /// l'empreinte du fichier.
    /// </summary>
    /// <remarks>
    /// L'UI edite le JSON reel plutot qu'un modele reconstitue : le fichier
    /// peut contenir des reglages qu'une version plus ancienne de l'UI ne
    /// connait pas, et les perdre a chaque enregistrement serait inacceptable.
    /// </remarks>
    private static IResult GetRaw(string name)
    {
        var root = ReadRoot();

        if (root is null)
        {
            return Results.Problem("Fichier de configuration illisible.");
        }

        var configuration = (root["Poller"]?["Configurations"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(c => string.Equals(
                c["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

        if (configuration is null)
        {
            return Results.NotFound(new { message = $"Configuration '{name}' introuvable." });
        }

        var copy = configuration.DeepClone().AsObject();
        MaskSecrets(copy);

        return Results.Ok(new
        {
            version = ConfigurationWriter.ComputeVersion(),
            configuration = copy,
        });
    }

    private static async Task<IResult> Upsert(
        string name,
        ConfigurationPayload payload,
        ConfigurationWriter writer,
        SettingsProtectionService protection,
        PollerHostedService workers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var result = writer.Upsert(name, payload.Configuration, payload.Version);

        if (!result.Success)
        {
            return Results.Conflict(result);
        }

        // Chiffrement APRES ecriture : l'UI envoie les nouveaux secrets en
        // clair, ils ne doivent pas rester ainsi sur disque une seconde de plus
        // que necessaire.
        await protection.ProtectAsync(ConfigurationWriter.SettingsPath, cancellationToken).ConfigureAwait(false);

        // Redemarrage du seul worker concerne : les autres boites continuent
        // de tourner pendant qu'on modifie celle-ci.
        await workers.RestartWorkerAsync(name, cancellationToken).ConfigureAwait(false);

        return Results.Ok(result with { Version = ConfigurationWriter.ComputeVersion() });
    }

    private static async Task<IResult> Delete(
        string name,
        string? version,
        ConfigurationWriter writer,
        PollerHostedService workers,
        CancellationToken cancellationToken)
    {
        var result = writer.Delete(name, version);

        if (!result.Success)
        {
            return Results.Conflict(result);
        }

        // Le worker est arrete par le redemarrage : la configuration n'existant
        // plus, RestartWorkerAsync arrete l'ancien sans en creer de nouveau.
        await workers.RestartWorkerAsync(name, cancellationToken).ConfigureAwait(false);

        return Results.Ok(result);
    }

    private static async Task<IResult> SetEnabled(
        string name,
        EnabledPayload payload,
        ConfigurationWriter writer,
        PollerHostedService workers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var result = writer.SetEnabled(name, payload.Enabled, payload.Version);

        if (!result.Success)
        {
            return Results.Conflict(result);
        }

        await workers.RestartWorkerAsync(name, cancellationToken).ConfigureAwait(false);

        return Results.Ok(result);
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
    /// Remplace les secrets par une chaine vide avant envoi a l'UI.
    /// </summary>
    /// <remarks>
    /// Vide et non "***" : l'UI renvoie l'objet tel quel a l'enregistrement, et
    /// le writer interprete un champ vide comme "inchange". Un masque litteral
    /// serait enregistre comme mot de passe.
    /// </remarks>
    private static void MaskSecrets(JsonObject node)
    {
        string[] secretFields = ["Password", "ClientSecret", "SecretKey", "Token", "ApiKey"];

        foreach (var key in node.Select(p => p.Key).ToArray())
        {
            if (node[key] is JsonObject nested)
            {
                MaskSecrets(nested);
                continue;
            }

            if (secretFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                node[key] = string.Empty;
            }
        }
    }
}

/// <summary>Corps d'une requete de modification de configuration.</summary>
/// <param name="Configuration">Contenu JSON de la configuration.</param>
/// <param name="Version">Empreinte du fichier lue par l'appelant.</param>
public sealed record ConfigurationPayload(JsonObject Configuration, string? Version);

/// <summary>Corps d'une requete d'activation.</summary>
/// <param name="Enabled">Nouvel etat.</param>
/// <param name="Version">Empreinte du fichier lue par l'appelant.</param>
public sealed record EnabledPayload(bool Enabled, string? Version);
