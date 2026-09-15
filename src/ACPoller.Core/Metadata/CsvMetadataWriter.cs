using System.Globalization;
using System.Text;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Options;

namespace ACPoller.Core.Metadata;

/// <summary>Reglages du fichier d'information CSV.</summary>
public sealed record CsvMetadataOptions
{
    /// <summary>
    /// Separateur de colonnes. Point-virgule par defaut : c'est ce qu'attend
    /// Excel en environnement francais, ou la virgule est le separateur decimal.
    /// </summary>
    public string Delimiter { get; init; } = ";";

    /// <summary>
    /// Ecrire la marque d'ordre des octets UTF-8. Necessaire pour qu'Excel
    /// affiche correctement les accents ; a desactiver si la GED cliente la
    /// prend pour des caracteres parasites en tete de la premiere colonne.
    /// </summary>
    public bool WriteBom { get; init; } = true;

    /// <summary>Ecrire la ligne d'en-tete.</summary>
    public bool WriteHeader { get; init; } = true;
}

/// <summary>
/// Produit le fichier d'information au format CSV, une ligne par piece.
/// </summary>
/// <remarks>
/// Format a plat : les colonnes de niveau message sont repetees sur chaque
/// ligne. C'est redondant, mais c'est ce qu'attendent les integrations qui
/// chargent le fichier directement en table, et cela evite une seconde passe
/// de jointure cote client.
/// </remarks>
public sealed class CsvMetadataWriter(IOptions<CsvMetadataOptions> options) : IMetadataWriter
{
    private static readonly string[] Columns =
    [
        "CorrelationId", "Configuration", "Mailbox", "MessageId", "Subject", "From",
        "ReceivedUtc", "ProcessedUtc", "Attempt",
        "Path", "FileName", "Kind", "ContentType", "SizeBytes", "Sha256",
        "Status", "Converted", "Substituted", "PageCount", "PdfFileName", "FailureReason",
    ];

    private readonly CsvMetadataOptions _options = options.Value;

    /// <inheritdoc />
    public string Format => "csv";

    /// <inheritdoc />
    public string FileExtension => "csv";

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        CaptureContext context,
        string outputDirectory,
        string fileNameWithoutExtension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = Path.Combine(outputDirectory, $"{fileNameWithoutExtension}.{FileExtension}");
        var temporary = path + ".tmp";

        Directory.CreateDirectory(outputDirectory);

        var manifest = CaptureManifest.Create(context);
        var customColumns = manifest.Fields.Keys.ToArray();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: _options.WriteBom);

        await using (var stream = File.Create(temporary))
        await using (var writer = new StreamWriter(stream, encoding))
        {
            if (_options.WriteHeader)
            {
                var header = Columns.Concat(customColumns);
                await writer.WriteLineAsync(string.Join(_options.Delimiter, header)).ConfigureAwait(false);
            }

            foreach (var item in manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(BuildRow(manifest, item, customColumns)).ConfigureAwait(false);
            }
        }

        File.Move(temporary, path, overwrite: true);

        return path;
    }

    private string BuildRow(CaptureManifest manifest, ManifestItem item, string[] customColumns)
    {
        string[] values =
        [
            manifest.CorrelationId,
            manifest.Configuration,
            manifest.Mailbox,
            manifest.MessageId,
            manifest.Subject ?? string.Empty,
            manifest.From ?? string.Empty,
            manifest.ReceivedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            manifest.ProcessedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            manifest.Attempt.ToString(CultureInfo.InvariantCulture),
            item.Path,
            item.FileName,
            item.Kind,
            item.ContentType ?? string.Empty,
            item.SizeBytes.ToString(CultureInfo.InvariantCulture),
            item.Sha256 ?? string.Empty,
            item.Status,
            item.Converted ? "1" : "0",
            item.Substituted ? "1" : "0",
            item.PageCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            item.PdfFileName ?? string.Empty,
            item.FailureReason ?? string.Empty,
        ];

        // Les champs personnalises sont repetes sur chaque ligne, comme les
        // autres colonnes de niveau message : le fichier reste chargeable
        // directement en table.
        var all = values.Concat(customColumns.Select(c =>
            manifest.Fields.TryGetValue(c, out var value) ? value : string.Empty));

        return string.Join(_options.Delimiter, all.Select(Escape));
    }

    /// <summary>
    /// Echappement RFC 4180. Le sujet d'un mail contient regulierement des
    /// point-virgules, des guillemets et des retours a la ligne : sans cet
    /// echappement, une seule ligne decale toutes les colonnes du fichier et
    /// l'integration cliente charge des donnees fausses sans lever d'erreur.
    /// </summary>
    private string Escape(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var needsQuotes = value.Contains(_options.Delimiter, StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
