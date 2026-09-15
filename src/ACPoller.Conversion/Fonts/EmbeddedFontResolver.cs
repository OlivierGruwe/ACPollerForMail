using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using PdfSharp.Fonts;

namespace ACPoller.Conversion.Fonts;

/// <summary>Reglages du resolveur de polices.</summary>
public sealed record FontResolverOptions
{
    /// <summary>
    /// Famille exposee au reste du code. Une seule suffit : les PDF produits ici
    /// sont des documents techniques, pas de la mise en page.
    /// </summary>
    public string FamilyName { get; init; } = "ACPollerSans";

    /// <summary>
    /// Repertoire contenant les fichiers TTF. Par defaut, les polices systeme
    /// Windows. Renseigner un repertoire livre avec l'application garantit un
    /// rendu identique sur tous les serveurs clients.
    /// </summary>
    public string FontDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    /// <summary>Fichier de la variante normale.</summary>
    public string RegularFile { get; init; } = "arial.ttf";

    /// <summary>Fichier de la variante grasse.</summary>
    public string BoldFile { get; init; } = "arialbd.ttf";

    /// <summary>Fichier de la variante italique.</summary>
    public string ItalicFile { get; init; } = "ariali.ttf";

    /// <summary>Fichier de la variante grasse italique.</summary>
    public string BoldItalicFile { get; init; } = "arialbi.ttf";
}

/// <summary>
/// Fournit les polices a PDFsharp depuis des fichiers TTF.
/// </summary>
/// <remarks>
/// PDFsharp 6 en version neutre n'accede pas aux polices systeme : sans
/// resolveur, toute ecriture de texte leve une exception. Ce resolveur est
/// donc obligatoire des lors qu'on produit un PDF contenant du texte, ce qui
/// inclut le PDF de substitution de la politique d'echec de conversion.
///
/// Recommandation de deploiement : livrer les fichiers de police avec
/// l'application plutot que de dependre des polices systeme. Arial est presente
/// sur tout Windows mais n'est pas redistribuable ; Liberation Sans, sous
/// licence SIL OFL, l'est et couvre les memes metriques. Un serveur Windows
/// Core minimal peut aussi ne pas avoir les polices attendues.
///
/// Le resolveur est global au processus : PDFsharp n'accepte qu'une instance,
/// affectee une seule fois au demarrage.
/// </remarks>
public sealed class EmbeddedFontResolver : IFontResolver
{
    private const string RegularFace = "regular";
    private const string BoldFace = "bold";
    private const string ItalicFace = "italic";
    private const string BoldItalicFace = "bolditalic";

    private static readonly Lock Gate = new();
    private static bool _registered;

    private readonly FontResolverOptions _options;
    private readonly ILogger<EmbeddedFontResolver> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Construit le resolveur.</summary>
    /// <param name="options">Reglages.</param>
    /// <param name="logger">Journalisation.</param>
    public EmbeddedFontResolver(FontResolverOptions options, ILogger<EmbeddedFontResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;
    }

    /// <summary>Famille a utiliser dans les appels a XFont.</summary>
    public string FamilyName => _options.FamilyName;

    /// <summary>
    /// Installe le resolveur globalement. Sans appel, toute ecriture de texte
    /// dans un PDF echoue. Idempotent : PDFsharp n'accepte qu'un resolveur par
    /// processus, une seconde affectation leverait.
    /// </summary>
    public void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            GlobalFontSettings.FontResolver = this;
            _registered = true;

            _logger.LogInformation(
                "Resolveur de polices installe : famille {Family}, repertoire {Directory}",
                _options.FamilyName, _options.FontDirectory);
        }
    }

    /// <summary>
    /// Verifie que les fichiers de police sont accessibles. A appeler au
    /// demarrage : une police manquante se decouvre autrement au premier
    /// document a convertir, en pleine production.
    /// </summary>
    /// <returns>Les fichiers manquants, vide si tout est en place.</returns>
    public IReadOnlyList<string> Verify()
    {
        string[] files =
        [
            _options.RegularFile, _options.BoldFile, _options.ItalicFile, _options.BoldItalicFile
        ];

        return
        [
            .. files
                .Select(f => Path.Combine(_options.FontDirectory, f))
                .Where(p => !File.Exists(p))
        ];
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // Toutes les familles demandees sont ramenees a la notre : le code
        // appelant n'a pas a connaitre le nom reel de la police installee.
        var face = (bold, italic) switch
        {
            (true, true) => BoldItalicFace,
            (true, false) => BoldFace,
            (false, true) => ItalicFace,
            _ => RegularFace,
        };

        return new FontResolverInfo(face);
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        return _cache.GetOrAdd(faceName, LoadFont);
    }

    private byte[] LoadFont(string faceName)
    {
        var fileName = faceName switch
        {
            BoldFace => _options.BoldFile,
            ItalicFace => _options.ItalicFile,
            BoldItalicFace => _options.BoldItalicFile,
            _ => _options.RegularFile,
        };

        var path = Path.Combine(_options.FontDirectory, fileName);

        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        // Repli sur la variante normale : un texte en gras rendu en normal
        // reste lisible, une exception fait perdre le document.
        var fallback = Path.Combine(_options.FontDirectory, _options.RegularFile);

        if (File.Exists(fallback))
        {
            _logger.LogWarning("Police {Face} introuvable, repli sur la variante normale", faceName);
            return File.ReadAllBytes(fallback);
        }

        // Dernier recours : une police embarquee dans l'assemblage, si elle a
        // ete ajoutee en ressource. Sinon, l'echec est explicite et designe la
        // cause reelle plutot qu'une exception PDFsharp incomprehensible.
        var embedded = TryLoadEmbedded(fileName);

        if (embedded is not null)
        {
            return embedded;
        }

        throw new FileNotFoundException(
            $"Police introuvable : '{path}'. Verifier FontDirectory, ou livrer les fichiers "
            + "de police avec l'application (Liberation Sans est redistribuable).",
            path);
    }

    private static byte[]? TryLoadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
