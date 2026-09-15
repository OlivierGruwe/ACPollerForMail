using System.Text.Json;
using System.Text.Json.Nodes;
using ACPoller.Abstractions.Infrastructure;

namespace ACPoller.Service.Security;

/// <summary>
/// Chiffre sur place les secrets laisses en clair dans le fichier de
/// configuration.
/// </summary>
/// <remarks>
/// Principe : un exploitant saisit un mot de passe en clair dans
/// appsettings.json, et au premier demarrage le service le remplace par sa
/// forme chiffree. Aucune manipulation prealable, aucun outil separe.
///
/// La detection se fait par NOM DE PROPRIETE, a n'importe quelle profondeur.
/// C'est volontaire : les reglages d'une cible d'export sont un dictionnaire
/// libre, leur chemin n'est pas connu a l'avance, et une liste de chemins
/// figes laisserait passer le mot de passe FTP de la troisieme cible d'une
/// configuration.
///
/// Limite a connaitre et a documenter cote client : DPAPI en portee machine
/// protege contre l'exfiltration du fichier, pas contre un processus tournant
/// sur la meme machine. Le complement indispensable est une ACL NTFS qui
/// reserve la lecture du fichier au compte de service et aux administrateurs.
/// </remarks>
public sealed class SettingsProtectionService(
    ISecretProtector protector,
    ILogger<SettingsProtectionService> logger)
{
    /// <summary>
    /// Noms de proprietes traites comme secrets, sans tenir compte de la casse.
    /// Ajouter un nom ici suffit a couvrir tous les emplacements ou il apparait.
    /// </summary>
    private static readonly string[] SecretPropertyNames =
    [
        "Password",
        "ClientSecret",
        "SecretKey",
        "ApiKey",
        "Token",
    ];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Parcourt le fichier et chiffre les secrets en clair. N'ecrit que si au
    /// moins une valeur a change.
    /// </summary>
    /// <param name="settingsPath">Chemin du fichier de configuration.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le nombre de secrets chiffres.</returns>
    public async Task<int> ProtectAsync(string settingsPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        if (!File.Exists(settingsPath))
        {
            logger.LogWarning("Fichier de configuration introuvable : {Path}", settingsPath);
            return 0;
        }

        JsonNode? root;

        await using (var stream = File.OpenRead(settingsPath))
        {
            root = await JsonNode.ParseAsync(
                stream,
                nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (root is null)
        {
            return 0;
        }

        var count = 0;
        Walk(root, ref count);

        if (count == 0)
        {
            return 0;
        }

        // Sauvegarde avant reecriture : si le chiffrement se passe mal, la
        // configuration d'origine reste recuperable. Elle contient les secrets
        // en clair, donc elle est ecrite a cote et doit etre supprimee par
        // l'exploitant une fois le demarrage valide.
        var backup = settingsPath + $".clear.{DateTime.Now:yyyyMMddHHmmss}.bak";
        File.Copy(settingsPath, backup, overwrite: false);

        var temporary = settingsPath + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, root, WriteOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, settingsPath, overwrite: true);

        logger.LogWarning(
            "{Count} secret(s) chiffre(s) dans {Path}. Sauvegarde EN CLAIR : {Backup} — "
            + "a supprimer apres validation du demarrage.",
            count, settingsPath, backup);

        return count;
    }

    private void Walk(JsonNode node, ref int count)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                // Copie des cles : la valeur d'une propriete est remplacee
                // pendant le parcours.
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var child = obj[key];

                    if (child is JsonValue value
                        && SecretPropertyNames.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (TryProtect(value, out var encrypted))
                        {
                            obj[key] = encrypted;
                            count++;
                        }

                        continue;
                    }

                    if (child is not null)
                    {
                        Walk(child, ref count);
                    }
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array.Where(i => i is not null))
                {
                    Walk(item!, ref count);
                }

                break;
            }

            default:
                break;
        }
    }

    private bool TryProtect(JsonValue value, out string encrypted)
    {
        encrypted = string.Empty;

        if (value.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        var plain = value.GetValue<string>();

        // Deja chiffre, ou vide : rien a faire. Le controle rend l'operation
        // idempotente, donc rejouable a chaque demarrage sans effet de bord.
        if (string.IsNullOrEmpty(plain) || protector.IsProtected(plain))
        {
            return false;
        }

        encrypted = protector.Protect(plain);
        return true;
    }
}
