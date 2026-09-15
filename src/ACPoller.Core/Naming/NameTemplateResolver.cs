using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;

namespace ACPoller.Core.Naming;

/// <summary>
/// Resout les jetons de nommage des fichiers de sortie.
/// </summary>
/// <remarks>
/// Moteur unique, partage par les writers et toutes les cibles d'export : deux
/// implementations divergentes produiraient des noms differents pour le meme
/// message selon la cible, ce qui rend toute reconciliation impossible.
///
/// Les jetons de date acceptent un format optionnel apres deux-points,
/// ex. <c>{date:yyyyMM}</c>. Sans format, le defaut du jeton s'applique.
/// </remarks>
public sealed partial class NameTemplateResolver : INameTemplateResolver
{
    private const int MaxSegmentLength = 120;

    private static readonly string[] KnownTokens =
    [
        "date", "time", "datetime", "received", "guid", "mailbox", "config",
        "subject", "from", "index", "name", "ext", "node",
    ];

    [GeneratedRegex(@"\{(?<token>[a-zA-Z]+)(?::(?<format>[^}]+))?\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern { get; }

    /// <inheritdoc />
    public string Resolve(string nameTemplate, CaptureContext context, DocumentNode? node = null, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(nameTemplate);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = TokenPattern.Replace(nameTemplate, match =>
        {
            var token = match.Groups["token"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;

            return ResolveToken(token, format, context, node, index) ?? match.Value;
        });

        return Sanitize(resolved);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(string nameTemplate)
    {
        if (string.IsNullOrWhiteSpace(nameTemplate))
        {
            return [];
        }

        return
        [
            .. TokenPattern.Matches(nameTemplate)
                .Select(m => m.Groups["token"].Value)
                .Where(t => !KnownTokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static string? ResolveToken(
        string token,
        string? format,
        CaptureContext context,
        DocumentNode? node,
        int? index)
    {
        var now = DateTimeOffset.Now;

        return token switch
        {
            // Heure locale et non UTC : ces noms sont lus par des exploitants,
            // pas par des machines. Un fichier date de 23h pour un mail recu
            // a 1h du matin serait incomprehensible en support.
            "date" => now.ToString(format ?? "yyyyMMdd", CultureInfo.InvariantCulture),
            "time" => now.ToString(format ?? "HHmmss", CultureInfo.InvariantCulture),
            "datetime" => now.ToString(format ?? "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),

            // Date de RECEPTION du message, stable entre deux tentatives,
            // contrairement a {date} qui change si le message est rejoue le lendemain.
            "received" => context.Envelope.ReceivedUtc.ToLocalTime()
                .ToString(format ?? "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),

            "guid" => Guid.NewGuid().ToString("N"),
            "mailbox" => LocalPart(context.MailboxId),
            "config" => context.ConfigurationName,
            "subject" => Truncate(
                string.IsNullOrWhiteSpace(context.Envelope.Subject) ? "sans-objet" : context.Envelope.Subject,
                60),
            "from" => LocalPart(context.Envelope.From ?? "inconnu"),
            // Chaine vide et non zero quand aucun indice n'est fourni : le PDF
            // unifie est un document de niveau MESSAGE, il n'a pas d'indice.
            // Le substituer par 000 lui donne le meme nom que la premiere
            // piece, qui l'ecrase ensuite silencieusement a l'export.
            "index" => index is null
                ? string.Empty
                : index.Value.ToString(format ?? "D3", CultureInfo.InvariantCulture),
            "name" => node is null
                ? Truncate(Path.GetFileNameWithoutExtension(context.Identity.DeduplicationKey), 40)
                : Truncate(Path.GetFileNameWithoutExtension(node.FileName), 60),
            "ext" => node is null ? string.Empty : Path.GetExtension(node.FileName).TrimStart('.'),
            "node" => node is null ? string.Empty : Truncate(node.LogicalPath.Replace('/', '_'), 60),

            // Jeton inconnu : la valeur d'origine est conservee telle quelle.
            // La validation l'a deja signale, inutile de faire echouer un
            // traitement en cours pour un probleme de parametrage.
            _ => null,
        };
    }

    /// <summary>Ne garde que la partie locale d'une adresse, pour raccourcir les noms.</summary>
    private static string LocalPart(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? address[..at] : address;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Retire tout ce qui ne peut pas figurer dans un nom de fichier Windows,
    /// y compris les separateurs de chemin : un sujet de mail contenant un
    /// slash ne doit jamais creer de sous-repertoire sur la cible.
    /// </summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;

        foreach (var c in value)
        {
            if (invalid.Contains(c) || c is '/' or '\\' or ':')
            {
                if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }

                continue;
            }

            builder.Append(c);
            lastWasSeparator = false;
        }

        var cleaned = builder.ToString().Trim('_', ' ', '.');

        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength];
        }

        // Separateurs consecutifs reduits, puis retires aux extremites : un
        // jeton non resolu laisse sinon des soulignes en trop dans le nom.
        while (cleaned.Contains("__", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim('_', ' ', '.');

        // Un nom vide produirait un fichier ".pdf" invisible et ingerable.
        return cleaned.Length == 0 ? "sans-nom" : cleaned;
    }
}
