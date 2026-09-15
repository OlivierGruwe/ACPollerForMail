using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Metadata;

/// <summary>
/// Produit le fichier d'information en appliquant une feuille XSLT cliente au
/// XML canonique.
/// </summary>
/// <remarks>
/// C'est la reponse au besoin recurrent d'une GED qui attend une structure
/// precise. Plutot que d'ajouter un writer par client, le service produit
/// toujours le meme XML et une feuille de style le transforme. Un nouveau
/// format d'integration devient un fichier livre, pas une livraison de code.
///
/// Le format de sortie n'est pas forcement du XML : une feuille declarant
/// <c>xsl:output method="text"</c> produit du plat, du CSV ou du largeur fixe.
/// C'est ce qui permet de couvrir les GED anciennes sans ecrire de code.
///
/// SECURITE : les scripts et la fonction document() sont DESACTIVES. Une
/// feuille XSLT est un fichier de configuration, et autoriser l'execution de
/// code depuis un fichier de configuration reviendrait a donner les droits du
/// compte de service a quiconque peut ecrire dans le repertoire.
/// </remarks>
public sealed class XsltMetadataWriter(ILogger<XsltMetadataWriter> logger) : IMetadataWriter
{
    /// <summary>
    /// Cle du sac de proprietes portant le chemin de la feuille de style.
    /// Renseignee par le pipeline depuis Output:Metadata:Template.
    /// </summary>
    public const string StylesheetKey = "__MetadataStylesheet";

    /// <summary>
    /// Cle du sac de proprietes portant l'extension du fichier produit.
    /// Renseignee depuis Output:Metadata:Extension.
    /// </summary>
    public const string ExtensionKey = "__MetadataExtension";

    // Feuilles compilees, indexees par chemin et date de modification : une
    // compilation coute cher, et refaire la compilation a chaque message
    // deviendrait le poste dominant sur un flux soutenu. La date dans la cle
    // fait qu'une feuille corrigee est reprise sans redemarrer le service.
    private readonly ConcurrentDictionary<string, XslCompiledTransform> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Format => "xslt";

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

        if (!context.Properties.TryGetValue(StylesheetKey, out var stylesheetPath)
            || string.IsNullOrWhiteSpace(stylesheetPath))
        {
            throw new AcPollerException(
                FailureKind.Permanent,
                "Format 'xslt' : le reglage Output:Metadata:Template doit designer une feuille de style.");
        }

        var extension = context.Properties.TryGetValue(ExtensionKey, out var configured)
            && !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimStart('.')
            : FileExtension;

        var path = Path.Combine(outputDirectory, $"{fileNameWithoutExtension}.{extension}");
        var temporary = path + ".tmp";

        Directory.CreateDirectory(outputDirectory);

        var transform = GetTransform(stylesheetPath);
        var source = BuildCanonicalXml(context);

        // La transformation est synchrone : XslCompiledTransform n'a pas
        // d'API asynchrone, et le travail est purement processeur sur un
        // document de quelques kilo-octets.
        await Task.Run(
            () =>
            {
                using var reader = source.CreateReader();
                using var stream = File.Create(temporary);

                // Les reglages de sortie viennent de la feuille : encodage,
                // methode, indentation. C'est elle qui connait ce qu'attend
                // la GED, pas le service.
                using var writer = XmlWriter.Create(stream, transform.OutputSettings);

                transform.Transform(reader, writer);
            },
            cancellationToken).ConfigureAwait(false);

        File.Move(temporary, path, overwrite: true);

        return path;
    }

    /// <summary>
    /// Verifie qu'une feuille de style est compilable, sans traiter de message.
    /// A appeler depuis le test de configuration : une feuille fautive ne doit
    /// pas se decouvrir sur la premiere facture.
    /// </summary>
    /// <param name="stylesheetPath">Chemin de la feuille.</param>
    /// <returns>Null si la feuille est valide, le motif sinon.</returns>
    public string? Validate(string stylesheetPath)
    {
        try
        {
            GetTransform(stylesheetPath);
            return null;
        }
        catch (Exception ex) when (ex is XsltException or XmlException or IOException)
        {
            return ex.Message;
        }
    }

    private XslCompiledTransform GetTransform(string stylesheetPath)
    {
        if (!File.Exists(stylesheetPath))
        {
            throw new AcPollerException(
                FailureKind.Permanent, $"Feuille de style introuvable : {stylesheetPath}");
        }

        var key = $"{stylesheetPath}|{File.GetLastWriteTimeUtc(stylesheetPath).Ticks}";

        return _cache.GetOrAdd(key, _ =>
        {
            var transform = new XslCompiledTransform(enableDebug: false);

            // XsltSettings.Default : scripts et document() DESACTIVES. Les
            // activer donnerait les droits du compte de service a quiconque
            // peut ecrire une feuille dans le repertoire de configuration.
            transform.Load(
                stylesheetPath,
                XsltSettings.Default,
                new XmlUrlResolver());

            logger.LogInformation("Feuille de style compilee : {Path}", stylesheetPath);

            return transform;
        });
    }

    /// <summary>
    /// Produit le XML canonique servant d'entree a la transformation.
    /// </summary>
    /// <remarks>
    /// Volontairement identique a celui de XmlMetadataWriter : une feuille
    /// ecrite pour l'un fonctionne sur l'autre, et un integrateur peut mettre
    /// le format en xml le temps de mettre au point sa feuille, puis basculer
    /// en xslt.
    /// </remarks>
    private static XDocument BuildCanonicalXml(CaptureContext context)
    {
        var manifest = CaptureManifest.Create(context);
        return XmlCanonicalBuilder.Build(manifest);
    }
}
