using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACPoller.Abstractions.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Configuration;

/// <summary>Issue d'une ecriture de configuration.</summary>
/// <param name="Success">Vrai si le fichier a ete modifie.</param>
/// <param name="Message">Message affichable.</param>
/// <param name="Version">Nouvelle empreinte du fichier, a renvoyer a la prochaine ecriture.</param>
/// <param name="Issues">Anomalies de validation, en cas de refus.</param>
public sealed record WriteResult(
    bool Success,
    string Message,
    string? Version = null,
    IReadOnlyList<ConfigurationIssue>? Issues = null)
{
    /// <summary>
    /// Configurations dont le parametrage effectif a change. Vide pour une
    /// modification de configuration, renseignee pour un gabarit : ses
    /// heritieres changent sans que leur propre bloc bouge.
    /// </summary>
    public IReadOnlyList<string> AffectedConfigurations { get; init; } = [];


}

/// <summary>
/// Modifie le fichier de configuration du service.
/// </summary>
/// <remarks>
/// Trois pieges sont traites ici, et aucun n'est optionnel :
///
/// 1. ECRITURE CONCURRENTE. Deux exploitants sur deux postes, ou un exploitant
///    et un editeur de texte. Chaque lecture renvoie une empreinte du fichier,
///    et l'ecriture la reclame : si le fichier a change entre-temps, la
///    modification est refusee au lieu d'ecraser silencieusement celle de
///    l'autre.
///
/// 2. SECRETS. L'UI ne recoit jamais les secrets, elle ne peut donc pas les
///    renvoyer. Un champ secret absent ou vide signifie INCHANGE : la valeur
///    chiffree deja presente est conservee telle quelle.
///
/// 3. FICHIER PARTIEL. Le fichier contient bien plus que les configurations :
///    chemins LibreOffice, jeton d'API, reglages de maintenance. On modifie le
///    NOEUD concerne dans l'arbre JSON existant, jamais on ne reecrit le
///    fichier a partir du modele objet, qui perdrait tout le reste.
/// </remarks>
public sealed class ConfigurationWriter(
    IConfiguration configuration,
    ConfigurationResolver resolver,
    ConfigurationValidator validator,
    RestartTracker restartTracker,
    ILogger<ConfigurationWriter> logger)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    private static readonly string[] SecretFields = ["Password", "ClientSecret", "SecretKey", "Token", "ApiKey"];

    /// <summary>Chemin du fichier de configuration du service.</summary>
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Empreinte du fichier, renvoyee a chaque lecture et exigee a l'ecriture.
    /// </summary>
    /// <returns>L'empreinte courante, ou une chaine vide si le fichier est absent.</returns>
    public static string ComputeVersion()
    {
        if (!File.Exists(SettingsPath))
        {
            return string.Empty;
        }

        using var stream = File.OpenRead(SettingsPath);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
    }

    /// <summary>
    /// Remplace les reglages de service, hors configurations et gabarits.
    /// </summary>
    /// <remarks>
    /// Les sections Configurations et Templates de l'arbre EXISTANT sont
    /// conservees : l'interface ne les envoie pas, et les prendre depuis le
    /// corps recu les effacerait toutes d'un coup.
    /// </remarks>
    /// <param name="payload">Reglages envoyes par l'interface.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public WriteResult UpdateServiceSettings(JsonObject payload, string? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                return new WriteResult(
                    false,
                    "Le fichier a ete modifie depuis sa lecture. Rafraichir puis reappliquer.");
            }

            var merged = payload.DeepClone().AsObject();

            // Les boites et les gabarits viennent de l'arbre existant, jamais
            // du corps recu : l'interface ne les transporte pas.
            if (root["Poller"] is JsonObject existingPoller)
            {
                var mergedPoller = merged["Poller"] as JsonObject ?? [];

                if (existingPoller["Configurations"] is JsonNode configurations)
                {
                    mergedPoller["Configurations"] = configurations.DeepClone();
                }

                if (existingPoller["Templates"] is JsonNode templates)
                {
                    mergedPoller["Templates"] = templates.DeepClone();
                }

                merged["Poller"] = mergedPoller;
            }

            if (root is JsonObject rootObject)
            {
                PreserveSecrets(rootObject, merged);
            }

            return ValidateAndSave(merged, "Reglages de service enregistres.");
        }
    }


    /// <summary>Cree ou remplace une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="payload">Contenu JSON de la configuration, tel qu'edite par l'UI.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant. Vide pour forcer.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public WriteResult Upsert(string name, JsonObject payload, string? expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);

        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                // Refus plutot qu'ecrasement : perdre la modification d'un
                // collegue sans qu'il le sache est pire que lui demander de
                // recommencer.
                return new WriteResult(
                    false,
                    "Le fichier a ete modifie depuis sa lecture. Rafraichir puis reappliquer la modification.");
            }

            var configurations = GetConfigurationsArray(root);
            var existing = FindByName(configurations, name);

            if (existing is not null)
            {
                PreserveSecrets(existing, payload);
            }

            // Le nom appartient au chemin, pas au corps : l'UI ne doit pas
            // pouvoir renommer une configuration par une simple ecriture, ce
            // qui en creerait une seconde sans supprimer la premiere.
            payload["Name"] = name;

            if (existing is not null)
            {
                var index = configurations.IndexOf(existing);
                configurations[index] = payload.DeepClone();
            }
            else
            {
                configurations.Add(payload.DeepClone());
            }

            return ValidateAndSave(root, $"Configuration '{name}' enregistree.");
        }
    }

    /// <summary>Supprime une configuration.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public WriteResult Delete(string name, string? expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                return new WriteResult(false, "Le fichier a ete modifie depuis sa lecture.");
            }

            var configurations = GetConfigurationsArray(root);
            var existing = FindByName(configurations, name);

            if (existing is null)
            {
                return new WriteResult(false, $"Configuration '{name}' introuvable.");
            }

            configurations.Remove(existing);

            return ValidateAndSave(root, $"Configuration '{name}' supprimee.");
        }
    }

    /// <summary>Active ou desactive une configuration sans toucher au reste.</summary>
    /// <param name="name">Nom de la configuration.</param>
    /// <param name="enabled">Nouvel etat.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant.</param>
    /// <returns>L'issue de l'ecriture.</returns>
    public WriteResult SetEnabled(string name, bool enabled, string? expectedVersion)
    {
        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                return new WriteResult(false, "Le fichier a ete modifie depuis sa lecture.");
            }

            var existing = FindByName(GetConfigurationsArray(root), name);

            if (existing is null)
            {
                return new WriteResult(false, $"Configuration '{name}' introuvable.");
            }

            existing["Enabled"] = enabled;

            return ValidateAndSave(root, $"Configuration '{name}' {(enabled ? "activee" : "desactivee")}.");
        }
    }

    /// <summary>
    /// Valide l'arbre modifie AVANT ecriture, puis enregistre.
    /// </summary>
    /// <remarks>
    /// La validation porte sur le resultat reel, apres resolution des gabarits,
    /// et non sur ce que l'UI croit avoir saisi. Une configuration qui herite
    /// d'un gabarit peut devenir invalide a cause d'une valeur qu'elle ne
    /// declare meme pas.
    /// </remarks>
    private WriteResult ValidateAndSave(JsonNode root, string successMessage)
    {
        var candidate = BuildCandidateConfiguration(root);

        if (candidate is null)
        {
            return new WriteResult(false, "Configuration resultante illisible.");
        }

        IReadOnlyList<ConfigurationIssue> issues;

        try
        {
            issues = validator.Validate(resolver.Resolve(candidate));
        }
        catch (Exception ex) when (ex is InvalidOperationException or AcPollerException)
        {
            return new WriteResult(false, $"Configuration invalide : {ex.Message}");
        }

        // Comparaison avant/apres : marquer a chaque enregistrement ferait
        // clignoter l'alerte en permanence, et elle serait ignoree en deux
        // jours.
        var previous = LoadRoot(out _);

        if (previous is not null)
        {
            foreach (var path in FindChangedPaths(previous, root, string.Empty))
            {
                restartTracker.Mark(path);
            }
        }

        var blocking = issues.Where(i => i.IsBlocking).ToArray();

        if (blocking.Length > 0)
        {
            // Rien n'est ecrit : une configuration bloquante sur disque serait
            // ecartee au demarrage suivant, et l'exploitant ne saurait pas
            // pourquoi sa boite ne tourne plus.
            return new WriteResult(false, "Configuration refusee.", Issues: blocking);
        }

        var backup = SettingsPath + $".{DateTime.Now:yyyyMMddHHmmss}.bak";
        File.Copy(SettingsPath, backup, overwrite: false);

        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions), Encoding.UTF8);
        File.Move(temporary, SettingsPath, overwrite: true);

        // Rechargement FORCE et synchrone : reloadOnChange est asynchrone et
        // debounce, un redemarrage de worker declenche juste apres lirait
        // encore l'ancienne configuration.
        if (configuration is IConfigurationRoot configurationRoot)
        {
            configurationRoot.Reload();
        }

        var version = ComputeVersion();

        logger.LogInformation("{Message} Sauvegarde : {Backup}", successMessage, backup);

        return new WriteResult(true, successMessage, version, issues.Where(i => !i.IsBlocking).ToArray());
    }

    /// <summary>
    /// Enumere les chemins dont la valeur a change entre deux arbres.
    /// </summary>
    /// <remarks>
    /// Comparaison structurelle et non textuelle : une reindentation du
    /// fichier ne doit pas passer pour une modification de reglage.
    /// La section Configurations est ignoree : elle est rechargeable a chaud
    /// par redemarrage de worker, c'est tout l'interet du decoupage.
    /// </remarks>
    private static IEnumerable<string> FindChangedPaths(JsonNode before, JsonNode after, string prefix)
    {
        if (before is not JsonObject beforeObject || after is not JsonObject afterObject)
        {
            if (before.ToJsonString() != after.ToJsonString())
            {
                yield return prefix;
            }

            yield break;
        }

        var keys = beforeObject.Select(p => p.Key)
            .Union(afterObject.Select(p => p.Key), StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (string.Equals(key, "Configurations", StringComparison.Ordinal)
                || string.Equals(key, "Templates", StringComparison.Ordinal))
            {
                continue;
            }

            var path = prefix.Length == 0 ? key : $"{prefix}:{key}";
            var beforeChild = beforeObject[key];
            var afterChild = afterObject[key];

            if (beforeChild is null || afterChild is null)
            {
                yield return path;
                continue;
            }

            foreach (var changed in FindChangedPaths(beforeChild, afterChild, path))
            {
                yield return changed;
            }
        }
    }

    /// <summary>
    /// Construit une IConfiguration a partir de l'arbre modifie, pour le
    /// valider sans avoir ecrit sur disque.
    /// </summary>
    private static IConfiguration? BuildCandidateConfiguration(JsonNode root)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
            return new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reporte les secrets deja presents quand l'UI ne les renvoie pas.
    /// </summary>
    /// <remarks>
    /// L'UI ne recoit jamais les secrets, elle ne peut donc pas les renvoyer.
    /// Un champ absent ou vide signifie INCHANGE. Sans ce report, chaque
    /// enregistrement depuis l'ecran effacerait le mot de passe de la boite,
    /// et la panne n'apparaitrait qu'au cycle suivant.
    /// </remarks>
    private static void PreserveSecrets(JsonObject existing, JsonObject payload)
    {
        foreach (var (key, existingValue) in existing)
        {
            if (existingValue is JsonObject nestedExisting && payload[key] is JsonObject nestedPayload)
            {
                PreserveSecrets(nestedExisting, nestedPayload);
                continue;
            }

            if (!SecretFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var incoming = payload[key]?.GetValue<string>();

            if (string.IsNullOrEmpty(incoming))
            {
                payload[key] = existingValue?.DeepClone();
            }
        }
    }

    private static JsonNode? LoadRoot(out string version)
    {
        version = ComputeVersion();

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(SettingsPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static JsonArray GetConfigurationsArray(JsonNode root)
    {
        var poller = root["Poller"] as JsonObject
            ?? throw new AcPollerException(FailureKind.Permanent, "Section 'Poller' absente.");

        if (poller["Configurations"] is not JsonArray configurations)
        {
            configurations = [];
            poller["Configurations"] = configurations;
        }

        return configurations;
    }

    private static JsonObject? FindByName(JsonArray configurations, string name) =>
        configurations
            .OfType<JsonObject>()
            .FirstOrDefault(c => string.Equals(
                c["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cree ou remplace un gabarit.</summary>
    /// <param name="name">Nom du gabarit.</param>
    /// <param name="payload">Contenu JSON du gabarit.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant.</param>
    /// <returns>L'issue de l'ecriture, avec les configurations touchees.</returns>
    public WriteResult UpsertTemplate(string name, JsonObject payload, string? expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);

        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                return new WriteResult(
                    false,
                    "Le fichier a ete modifie depuis sa lecture. Rafraichir puis reappliquer.");
            }

            var poller = root["Poller"] as JsonObject
                ?? throw new AcPollerException(FailureKind.Permanent, "Section 'Poller' absente.");

            if (poller["Templates"] is not JsonObject templates)
            {
                templates = [];
                poller["Templates"] = templates;
            }

            if (templates[name] is JsonObject existing)
            {
                PreserveSecrets(existing, payload);
            }

            // Un gabarit ne porte pas de nom dans son corps : sa cle EST son
            // nom. Laisser un Name dedans le ferait heriter par toutes ses
            // configurations, qui porteraient alors le meme.
            payload.Remove("Name");

            templates[name] = payload.DeepClone();

            var affected = AffectedByTemplate(poller, name);
            var result = ValidateAndSave(root, $"Gabarit '{name}' enregistre.");

            return result.Success ? result with { AffectedConfigurations = affected } : result;
        }
    }

    /// <summary>Supprime un gabarit, s'il n'est plus utilise.</summary>
    /// <param name="name">Nom du gabarit.</param>
    /// <param name="expectedVersion">Empreinte lue par l'appelant.</param>
    /// <returns>L'issue de la suppression.</returns>
    public WriteResult DeleteTemplate(string name, string? expectedVersion)
    {
        lock (Gate)
        {
            var root = LoadRoot(out var currentVersion);

            if (root is null)
            {
                return new WriteResult(false, "Fichier de configuration illisible.");
            }

            if (!string.IsNullOrEmpty(expectedVersion) && expectedVersion != currentVersion)
            {
                return new WriteResult(false, "Le fichier a ete modifie depuis sa lecture.");
            }

            var poller = root["Poller"] as JsonObject;

            if (poller?["Templates"] is not JsonObject templates || templates[name] is null)
            {
                return new WriteResult(false, $"Gabarit '{name}' introuvable.");
            }

            var affected = AffectedByTemplate(poller, name);

            // Refus plutot que suppression en cascade : supprimer un gabarit
            // utilise rendrait toutes ses heritieres invalides d'un coup, et
            // le service les ecarterait au demarrage suivant sans que
            // l'exploitant comprenne pourquoi.
            if (affected.Count > 0)
            {
                return new WriteResult(
                    false,
                    $"Gabarit utilise par {affected.Count} configuration(s) : "
                    + $"{string.Join(", ", affected)}. Les detacher avant de le supprimer.");
            }

            templates.Remove(name);

            return ValidateAndSave(root, $"Gabarit '{name}' supprime.");
        }
    }

    private static IReadOnlyList<string> AffectedByTemplate(JsonObject poller, string templateName) =>
    [
        .. (poller["Configurations"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => string.Equals(
                c["Template"]?.GetValue<string>(), templateName, StringComparison.OrdinalIgnoreCase))
            .Select(c => c["Name"]?.GetValue<string>() ?? string.Empty)
            .Where(n => n.Length > 0)
    ];
}
