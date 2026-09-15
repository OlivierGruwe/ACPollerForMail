using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;

namespace ACPoller.Core.Metadata;

/// <summary>Produit le fichier d'information au format JSON.</summary>
public sealed class JsonMetadataWriter : IMetadataWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Encodeur permissif : sans lui, les accents et les caracteres non
        // ASCII sortent en sequences \uXXXX. C'est valide, mais illisible pour
        // l'exploitant qui ouvre le fichier en support, et certains parseurs
        // de GED anciens ne les interpretent pas.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public string Format => "json";

    /// <inheritdoc />
    public string FileExtension => "json";

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

        // Ecriture atomique, comme pour les PDF : une GED qui scrute le
        // repertoire ne doit jamais lire un JSON tronque.
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, Options, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        return path;
    }
}
