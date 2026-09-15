using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ACPoller.Conversion;

/// <summary>
/// Assainit les noms de fichiers issus de sources non fiables.
/// </summary>
/// <remarks>
/// Centralise ici parce que cette fonction etait recopiee dans le parseur, les
/// trois extracteurs et les convertisseurs, avec des comportements legerement
/// differents. Un nom de fichier arrive par mail, donc de n'importe qui : il
/// n'y a aucune raison que sa validation depende du composant qui le traite.
///
/// Deux garanties, et elles suffisent :
/// 1. Le resultat ne peut pas sortir du repertoire cible : ni separateur, ni
///    sequence de remontee, ni nom reserve Windows.
/// 2. Le resultat est un nom de fichier valide sur Windows, de longueur bornee.
/// </remarks>
public static class FileNameSanitizer
{
    private const int MaxLength = 100;
    private const int MaxStemLength = 90;

    /// <summary>
    /// Noms reserves par Windows, sans extension. Un fichier nomme CON.pdf est
    /// impossible a creer, et l'erreur ne designe pas la cause.
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Assainit un nom de fichier.</summary>
    /// <param name="name">Nom d'origine, potentiellement hostile.</param>
    /// <param name="fallback">Nom retenu si le resultat est vide.</param>
    /// <returns>Un nom de fichier sur, de longueur bornee.</returns>
    public static string Sanitize(string? name, string fallback = "piece.bin")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Seul le nom de fichier est conserve : un chemin complet, absolu ou
        // relatif, est reduit a son dernier segment.
        var candidate = name.Replace('\\', '/');
        var lastSlash = candidate.LastIndexOf('/');

        if (lastSlash >= 0)
        {
            candidate = candidate[(lastSlash + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(candidate.Length);

        foreach (var c in candidate)
        {
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var cleaned = builder.ToString();

        // Sequences de points reduites a un seul : ".." n'est pas un caractere
        // interdit, mais le laisser produit des noms trompeurs en GED, et un
        // nom entierement compose de points est refuse par Windows.
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        // Points et espaces en tete ou en fin : Windows les retire
        // silencieusement a la creation, ce qui fait diverger le nom stocke
        // dans le fichier d'information du nom reellement ecrit sur disque.
        cleaned = cleaned.Trim(' ', '.', '_');

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        cleaned = AvoidReservedName(cleaned);

        return Truncate(cleaned, fallback);
    }

    /// <summary>
    /// Assainit un chemin logique pour en faire un segment de nom de fichier.
    /// Les separateurs deviennent des soulignes, la hierarchie reste lisible.
    /// </summary>
    /// <param name="logicalPath">Chemin logique, ex. "transfert.eml/bl.pdf".</param>
    /// <returns>Un segment utilisable dans un nom de fichier.</returns>
    public static string SanitizeSegment(string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);

        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string([.. logicalPath.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' || char.IsControl(c) ? '_' : c)]);

        cleaned = cleaned.Trim(' ', '.', '_');

        return cleaned.Length == 0
            ? "piece"
            : cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    /// <summary>
    /// Rend un nom unique dans un ensemble deja utilise.
    /// </summary>
    /// <remarks>
    /// Deux pieces jointes de meme nom dans un meme message, c'est courant :
    /// sans desambiguisation, la seconde ecrase la premiere en silence.
    /// </remarks>
    /// <param name="name">Nom souhaite, deja assaini.</param>
    /// <param name="used">Noms deja attribues. Le nom retenu y est ajoute.</param>
    /// <returns>Un nom qui n'entre pas en collision.</returns>
    public static string MakeUnique(string name, HashSet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{extension}";

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string AvoidReservedName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);

        if (!ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }

        // Le suffixe est ajoute au radical, pas au nom complet : l'extension
        // doit rester en fin, sinon plus aucun convertisseur ne reconnait le
        // format.
        return $"{stem}_{Path.GetExtension(name)}";
    }

    private static string Truncate(string name, string fallback)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        // L'extension est preservee en priorite : c'est elle qui determine le
        // convertisseur. Une extension aberrante de plus de dix caracteres est
        // en revanche abandonnee, ce n'en est probablement pas une.
        if (extension.Length > 10)
        {
            extension = string.Empty;
            stem = name;
        }

        var keep = Math.Min(MaxStemLength, stem.Length);

        var truncated = stem[..keep] + extension;

        return truncated.Trim(' ', '.', '_') is { Length: > 0 } result ? result : fallback;
    }
}
