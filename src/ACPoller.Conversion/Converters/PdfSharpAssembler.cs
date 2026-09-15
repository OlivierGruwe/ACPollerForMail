using System.Diagnostics;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ACPoller.Conversion.Converters;

/// <summary>Reglages de l'assembleur PDF.</summary>
public sealed record PdfAssemblerOptions
{
    /// <summary>Chemin de l'executable qpdf, utilise pour reparer les PDF corrompus. Null pour desactiver.</summary>
    public string? QpdfPath { get; init; }

    /// <summary>Delai maximal accorde a une reparation.</summary>
    public TimeSpan RepairTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Ajouter un signet par piece dans le PDF unifie.</summary>
    public bool AddBookmarks { get; init; } = true;
}

/// <summary>
/// Fusionne et repare les PDF, sur PDFsharp.
/// </summary>
/// <remarks>
/// PDFsharp est sous licence MIT, contrairement a iText7 qui est en AGPL et
/// donc inutilisable dans un produit livre client.
///
/// La reparation delegue a qpdf, appele en ligne de commande (Apache 2.0, pas
/// de liaison). PDFsharp echoue sur les PDF structurellement invalides, alors
/// que qpdf reconstruit la table des references croisees dans la plupart des
/// cas. Sans lui, un scanner qui produit des PDF legerement hors norme fait
/// echouer un flux entier.
/// </remarks>
public sealed class PdfSharpAssembler(PdfAssemblerOptions options, ILogger<PdfSharpAssembler> logger) : IPdfAssembler
{
    /// <inheritdoc />
    public async Task<string> MergeAsync(
        IReadOnlyList<DocumentNode> parts,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var merged = new PdfDocument();
        merged.Info.Title = Path.GetFileNameWithoutExtension(outputPath);
        merged.Info.Creator = "ACPoller";

        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (part.PdfPath is null || !File.Exists(part.PdfPath))
            {
                continue;
            }

            var source = await OpenForImportAsync(part.PdfPath, cancellationToken).ConfigureAwait(false);

            if (source is null)
            {
                // La piece est perdue pour le PDF unifie, mais le message
                // continue : un document corrompu sur dix ne doit pas faire
                // echouer les neuf autres.
                logger.LogWarning("Piece ignoree dans la fusion : {Path}", part.PdfPath);
                continue;
            }

            using (source)
            {
                var firstPageIndex = merged.PageCount;

                for (var i = 0; i < source.PageCount; i++)
                {
                    merged.AddPage(source.Pages[i]);
                }

                if (options.AddBookmarks && source.PageCount > 0 && merged.PageCount > firstPageIndex)
                {
                    // Un signet par piece : sur un mail a vingt pieces jointes,
                    // c'est la difference entre un PDF exploitable et un bloc
                    // de deux cents pages ou personne ne retrouve rien.
                    merged.Outlines.Add(part.FileName, merged.Pages[firstPageIndex], true);
                }
            }
        }

        if (merged.PageCount == 0)
        {
            // PDFsharp refuse d'enregistrer un document sans page. Une page
            // vide vaut mieux qu'une exception : la GED recevra un document,
            // et le fichier d'information portera le detail des echecs.
            merged.AddPage();
            logger.LogWarning("Fusion sans aucune page exploitable : {Output}", outputPath);
        }

        // Idem : lecture avant Save, sinon EnsureNotYetSaved leve.
        var pageCount = merged.PageCount;

        var temporary = outputPath + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await merged.SaveAsync(stream, false).ConfigureAwait(false);
        }

        File.Move(temporary, outputPath, overwrite: true);

        logger.LogDebug("PDF unifie produit : {Output} ({Pages} pages)", outputPath, pageCount);

        return outputPath;
    }

    /// <inheritdoc />
    public async Task<string?> RepairAsync(string pdfPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        if (string.IsNullOrWhiteSpace(options.QpdfPath) || !File.Exists(options.QpdfPath))
        {
            logger.LogDebug("Reparation impossible : qpdf non configure ou introuvable");
            return null;
        }

        var repaired = Path.Combine(
            Path.GetDirectoryName(pdfPath)!,
            Path.GetFileNameWithoutExtension(pdfPath) + ".repaired.pdf");

        var startInfo = new ProcessStartInfo
        {
            FileName = options.QpdfPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        // qpdf reconstruit la table des references croisees, ce qui suffit dans
        // la grande majorite des cas de PDF produits par des scanners.
        // --replace-input est EXCLUSIF d'un fichier de sortie : on ecrit dans
        // un nouveau fichier pour ne jamais alterer la piece d'origine, qui
        // reste la reference en cas de litige.
        startInfo.ArgumentList.Add("--qdf");
        startInfo.ArgumentList.Add("--object-streams=disable");
        startInfo.ArgumentList.Add(pdfPath);
        startInfo.ArgumentList.Add(repaired);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RepairTimeout);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            logger.LogWarning("Reparation abandonnee apres {Timeout} : {Path}", options.RepairTimeout, pdfPath);
            return null;
        }

        // qpdf retourne 3 pour un avertissement, ce qui reste exploitable :
        // seul un code superieur signale un echec reel.
        if (process.ExitCode > 3 || !File.Exists(repaired))
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Reparation echouee (code {Code}) : {Error}", process.ExitCode, error.Trim());
            return null;
        }

        logger.LogInformation("PDF repare : {Path}", pdfPath);
        return repaired;
    }

    private async Task<PdfDocument?> OpenForImportAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "Ouverture directe impossible, tentative de reparation : {Path}", path);
        }

        var repaired = await RepairAsync(path, cancellationToken).ConfigureAwait(false);

        if (repaired is null)
        {
            return null;
        }

        try
        {
            return PdfReader.Open(repaired, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "PDF irrecuperable meme apres reparation : {Path}", path);
            return null;
        }
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Arbre complet : qpdf peut avoir des enfants selon la plateforme.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            logger.LogDebug(ex, "Processus deja termine");
        }
    }
}
