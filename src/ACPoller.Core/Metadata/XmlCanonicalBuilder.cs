using System.Globalization;
using System.Xml.Linq;

namespace ACPoller.Core.Metadata;

/// <summary>
/// Construit le XML canonique du fichier d'information.
/// </summary>
/// <remarks>
/// Extrait du writer XML pour etre partage avec le writer XSLT : les deux
/// doivent produire EXACTEMENT la meme structure d'entree. Une feuille de
/// style ecrite en regardant la sortie xml doit fonctionner telle quelle en
/// format xslt, sinon la mise au point devient impossible.
/// </remarks>
public static class XmlCanonicalBuilder
{
    /// <summary>Construit le document canonique.</summary>
    /// <param name="manifest">Projection du message.</param>
    /// <returns>Le document XML.</returns>
    public static XDocument Build(CaptureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Le nom du champ est un ATTRIBUT et non un nom d'element : un nom
        // saisi par un exploitant peut contenir un espace, un accent ou
        // commencer par un chiffre, ce qui produirait un XML invalide.
        var fields = new XElement("Fields",
            manifest.Fields.Select(f =>
                new XElement("Field",
                    new XAttribute("name", f.Key),
                    f.Value)));

        var properties = new XElement("Properties",
            manifest.Properties.Select(p =>
                new XElement("Property",
                    new XAttribute("name", p.Key),
                    p.Value)));

        var items = new XElement("Items",
            manifest.Items.Select(item =>
                new XElement("Item",
                    new XAttribute("path", item.Path),
                    new XElement("FileName", item.FileName),
                    new XElement("Kind", item.Kind),
                    Optional("ContentType", item.ContentType),
                    new XElement("SizeBytes", item.SizeBytes.ToString(CultureInfo.InvariantCulture)),
                    Optional("Sha256", item.Sha256),
                    new XElement("Status", item.Status),
                    new XElement("Converted", item.Converted ? "true" : "false"),
                    new XElement("Substituted", item.Substituted ? "true" : "false"),
                    item.PageCount is null
                        ? null
                        : new XElement("PageCount", item.PageCount.Value.ToString(CultureInfo.InvariantCulture)),
                    Optional("PdfFileName", item.PdfFileName),
                    Optional("FailureReason", item.FailureReason))));

        var warnings = new XElement("Warnings",
            manifest.Warnings.Select(w => new XElement("Warning", w)));

        // Ordre des blocs : Fields porte ce que le client a explicitement
        // demande en sortie, Properties ce que les traitements metier ont
        // depose au passage. L'ordre reflete l'importance pour celui qui lit
        // le fichier, et il est fige des la premiere integration cliente.
        var root = new XElement("Capture",
            new XAttribute("version", manifest.Version),
            new XElement("CorrelationId", manifest.CorrelationId),
            new XElement("Configuration", manifest.Configuration),
            new XElement("Mailbox", manifest.Mailbox),
            new XElement("MessageId", manifest.MessageId),
            Optional("Subject", manifest.Subject),
            Optional("From", manifest.From),
            new XElement("To", manifest.To.Select(t => new XElement("Recipient", t))),
            new XElement("Cc", manifest.Cc.Select(t => new XElement("Recipient", t))),
            new XElement("ReceivedUtc", manifest.ReceivedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            new XElement("ProcessedUtc", manifest.ProcessedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            new XElement("Attempt", manifest.Attempt.ToString(CultureInfo.InvariantCulture)),
            new XElement("FailedCount", manifest.FailedCount.ToString(CultureInfo.InvariantCulture)),
            fields,
            properties,
            items,
            warnings);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>
    /// Retourne null pour une valeur absente : LINQ to XML ignore les null, ce
    /// qui evite un element vide qu'une GED pourrait prendre pour une chaine
    /// vide plutot que pour une absence de valeur.
    /// </summary>
    private static XElement? Optional(string name, string? value) =>
        string.IsNullOrEmpty(value) ? null : new XElement(name, value);
}
