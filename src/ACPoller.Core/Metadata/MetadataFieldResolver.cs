using System.Globalization;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using ACPoller.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Metadata;

/// <summary>
/// Calcule les champs personnalises ajoutes au fichier d'information.
/// </summary>
/// <remarks>
/// Un champ non resolu ne fait JAMAIS echouer le traitement. Il produit sa
/// valeur par defaut, ou une chaine vide, et un avertissement. Perdre un
/// message parce qu'un en-tete attendu manque sur un mail atypique serait
/// disproportionne : c'est le document qui compte, pas la completude de son
/// index.
/// </remarks>
public sealed class MetadataFieldResolver(
    INameTemplateResolver nameResolver,
    ILogger<MetadataFieldResolver> logger)
{
    /// <summary>Resout tous les champs declares pour une configuration.</summary>
    /// <param name="fields">Champs declares.</param>
    /// <param name="context">Contexte du message.</param>
    /// <returns>Les champs resolus, dans l'ordre de declaration.</returns>
    public IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyList<MetadataFieldOptions> fields,
        CaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(context);

        // Dictionnaire ordonne : l'ordre de declaration est celui des colonnes
        // du CSV, et une GED qui lit du positionnel n'y survivrait pas s'il
        // changeait d'un message a l'autre.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            var value = ResolveOne(field, context);
            resolved[field.Name] = value;
        }

        return resolved;
    }

    private string ResolveOne(MetadataFieldOptions field, CaptureContext context)
    {
        string raw;

        try
        {
            raw = field.Source switch
            {
                FieldSource.Fixed => field.Value,
                FieldSource.Token => nameResolver.Resolve(field.Value, context),
                FieldSource.Header => ReadHeader(field.Value, context),
                FieldSource.Property => context.Properties.TryGetValue(field.Value, out var property)
                    ? property
                    : string.Empty,
                FieldSource.Environment => Environment.GetEnvironmentVariable(field.Value) ?? string.Empty,
                _ => string.Empty,
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Un gabarit mal forme degrade le champ, il n'arrete pas le message.
            logger.LogWarning(
                ex, "Champ {Field} non resolu pour {CorrelationId}", field.Name, context.CorrelationId);

            raw = string.Empty;
        }

        raw = ApplyFormat(field, raw);
        raw = ApplyTranslation(field, raw);

        if (string.IsNullOrEmpty(raw) && field.Default is not null)
        {
            raw = field.Default;
        }

        return ApplyFixedLength(field, raw);
    }

    /// <summary>
    /// Lit un en-tete du message. Le nom est insensible a la casse, comme
    /// l'exige la RFC 5322 : un relais peut ecrire X-Company ou x-company.
    /// </summary>
    private static string ReadHeader(string name, CaptureContext context) =>
        context.Envelope.Headers.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>
    /// Applique un format de date quand la valeur en est une. Une valeur non
    /// datee traverse sans modification : declarer un format sur un champ
    /// texte ne doit pas le vider.
    /// </summary>
    private static string ApplyFormat(MetadataFieldOptions field, string value)
    {
        if (string.IsNullOrWhiteSpace(field.Format) || string.IsNullOrEmpty(value))
        {
            return value;
        }

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToString(field.Format, CultureInfo.InvariantCulture)
            : value;
    }

    private static string ApplyTranslation(MetadataFieldOptions field, string value) =>
        field.Values.Count > 0 && field.Values.TryGetValue(value, out var translated)
            ? translated
            : value;

    private static string ApplyFixedLength(MetadataFieldOptions field, string value)
    {
        if (field.FixedLength <= 0)
        {
            return value;
        }

        if (value.Length > field.FixedLength)
        {
            // Troncature a droite : sur un format positionnel, deborder decale
            // toutes les colonnes suivantes, ce qui est pire que perdre la fin
            // d'un libelle.
            return value[..field.FixedLength];
        }

        return field.PadLeft
            ? value.PadLeft(field.FixedLength, field.PaddingChar)
            : value.PadRight(field.FixedLength, field.PaddingChar);
    }
}
