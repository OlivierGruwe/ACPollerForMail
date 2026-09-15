using System.Text.Json.Nodes;
using ACPoller.Core.Configuration;
using ACPoller.Core.Workers;
using ACPoller.Service.Security;

namespace ACPoller.Service.Control;

/// <summary>
/// Points d'entree de gestion des gabarits.
/// </summary>
/// <remarks>
/// Un gabarit a exactement la meme forme qu'une configuration : c'est le meme
/// type dans le modele, et le meme formulaire peut donc l'editer. Ce qui
/// change est la portee d'une modification : toucher a un gabarit modifie
/// TOUTES les configurations qui en heritent, potentiellement des dizaines.
/// C'est la raison d'etre des deux precautions prises ici.
/// </remarks>
public static class TemplateEndpoints
{
    /// <summary>Declare les points d'entree de gestion des gabarits.</summary>
    /// <param name="group">Groupe de routes deja protege par le filtre de jeton.</param>
    /// <returns>Le groupe, pour le chainage.</returns>
    public static RouteGroupBuilder MapTemplateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/templates", GetTemplates);
        group.MapGet("/templates/{name}/raw", GetRaw);
        group.MapPut("/templates/{name}", Upsert);
        group.MapDelete("/templates/{name}", Delete);

        return group;
    }

    /// <summary>
    /// Liste les gabarits avec le nombre de configurations qui en heritent.
    /// </summary>
    /// <remarks>
    /// Le compteur n'est pas decoratif : c'est la portee d'une modification, et
    /// ce qui empeche de supprimer un gabarit encore utilise sans le savoir.
    /// </remarks>
    private static IResult GetTemplates()
    {
        var root = ReadRoot();

        if (root?["Poller"] is not JsonObject poller)
        {
            return Results.Problem("Fichier de configuration illisible.");
        }

        var templates = poller["Templates"] as JsonObject ?? [];
        var configurations = poller["Configurations"] as JsonArray ?? [];

        var payload = templates.Select(t => new TemplateSummary
        {
            Name = t.Key,
            UsedBy =
            [
                .. configurations
                    .OfType<JsonObject>()
                    .Where(c => string.Equals(
                        c["Template"]?.GetValue<string>(), t.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c["Name"]?.GetValue<string>() ?? string.Empty)
            ],
            Protocol = (t.Value as JsonObject)?["Source"]?["Protocol"]?.GetValue<string>(),
        });

        return Results.Ok(new
        {
            version = ConfigurationWriter.ComputeVersion(),
            templates = payload,
        });
    }

    private static IResult GetRaw(string name)
    {
        var root = ReadRoot();

        if (root?["Poller"]?["Templates"] is not JsonObject templates)
        {
            return Results.Problem("Fichier de configuration illisible.");
        }

        if (templates[name] is not JsonObject template)
        {
            return Results.NotFound(new { message = $"Gabarit '{name}' introuvable." });
        }

        var copy = template.DeepClone().AsObject();
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

        var result = writer.UpsertTemplate(name, payload.Configuration, payload.Version);

        if (!result.Success)
        {
            return Results.Conflict(result);
        }

        await protection.ProtectAsync(ConfigurationWriter.SettingsPath, cancellationToken)
            .ConfigureAwait(false);

        // TOUTES les configurations heritant du gabarit sont redemarrees : leur
        // parametrage effectif vient de changer, meme si leur propre bloc n'a
        // pas bouge. Ne redemarrer aucune laisserait les workers tourner sur
        // l'ancienne configuration jusqu'au prochain redemarrage du service.
        foreach (var configuration in result.AffectedConfigurations)
        {
            await workers.RestartWorkerAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(result with { Version = ConfigurationWriter.ComputeVersion() });
    }

    private static IResult Delete(string name, string? version, ConfigurationWriter writer)
    {
        var result = writer.DeleteTemplate(name, version);

        return result.Success ? Results.Ok(result) : Results.Conflict(result);
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
    /// Vide et non un masque litteral : le writer interprete un champ vide
    /// comme "inchange", un masque serait enregistre comme mot de passe.
    /// </summary>
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

/// <summary>Resume d'un gabarit, avec sa portee.</summary>
public sealed record TemplateSummary
{
    /// <summary>Nom du gabarit.</summary>
    public required string Name { get; init; }

    /// <summary>Configurations qui en heritent.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>Protocole declare, a titre indicatif.</summary>
    public string? Protocol { get; init; }
}
