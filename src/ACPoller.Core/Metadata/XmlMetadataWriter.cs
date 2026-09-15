using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;

namespace ACPoller.Core.Metadata;

/// <summary>
/// Produit le fichier d'information au format XML.
/// </summary>
/// <remarks>
/// Construction par XDocument plutot que par XmlSerializer : le contrat avec la
/// GED est explicite dans le code, nom d'element par nom d'element. Avec un
/// serialiseur, renommer une propriete C# changerait silencieusement le format
/// de sortie et casserait l'integration client.
/// </remarks>
public sealed class XmlMetadataWriter : IMetadataWriter
{
    /// <inheritdoc />
    public string Format => "xml";

    /// <inheritdoc />
    public string FileExtension => "xml";

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
        var document = Build(manifest);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Async = true,

            // Pas de marque d'ordre des octets : plusieurs GED anciennes la
            // prennent pour des caracteres parasites en tete de fichier et
            // refusent le document.
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        // Ecriture atomique, comme pour les PDF : une GED qui scrute le
        // repertoire ne doit jamais lire un XML tronque.
        await using (var stream = File.Create(temporary))
        await using (var writer = XmlWriter.Create(stream, settings))
        {
            await document.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        return path;
    }

    private static XDocument Build(CaptureManifest manifest) =>
        XmlCanonicalBuilder.Build(manifest);
}
