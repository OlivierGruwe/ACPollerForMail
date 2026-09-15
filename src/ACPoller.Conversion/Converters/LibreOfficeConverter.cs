using System.Diagnostics;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Conversion;
using Microsoft.Extensions.Logging;

namespace ACPoller.Conversion.Converters;

/// <summary>Reglages du convertisseur LibreOffice.</summary>
public sealed record LibreOfficeOptions
{
    /// <summary>Chemin de soffice.exe.</summary>
    public required string SofficePath { get; init; }

    /// <summary>Repertoire racine des profils utilisateur isoles.</summary>
    public required string ProfileRootDirectory { get; init; }

    /// <summary>Nombre de conversions LibreOffice simultanees.</summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>Filtre PDF passe a LibreOffice. Null pour le filtre par defaut.</summary>
    public string? PdfFilter { get; init; }
}

/// <summary>
/// Convertit les formats bureautiques en PDF via LibreOffice, hors process.
/// </summary>
/// <remarks>
/// Trois regles tirees de l'experience v1, chacune non negociable :
///
/// 1. PROFIL ISOLE PAR SLOT. LibreOffice verrouille son profil utilisateur :
///    deux instances partageant le meme profil se bloquent mutuellement, ou pire,
///    la seconde s'arrete silencieusement en croyant qu'une instance tourne deja.
///    Chaque slot du pool a donc son propre repertoire, passe en -env:UserInstallation.
///
/// 2. TIMEOUT QUI TUE, PAS QUI ATTEND. Un LibreOffice bloque sur un document
///    corrompu ne se debloque jamais. Au depassement, l'arbre de processus est
///    tue. C'est la difference entre une piece jointe perdue et un service fige.
///
/// 3. VERIFICATION DU FICHIER PRODUIT. LibreOffice retourne regulierement un
///    code 0 sans avoir rien ecrit. Le code de sortie ne prouve rien, seule
///    l'existence du PDF fait foi.
///
/// Une interaction avec Word ou Excel par Interop est a proscrire sur serveur :
/// Microsoft ne la supporte pas hors session interactive, et elle laisse des
/// processus orphelins.
/// </remarks>
public sealed class LibreOfficeConverter : IDocumentConverter, IDisposable
{
    private static readonly string[] SupportedExtensions =
    [
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf",
        ".xls", ".xlsx", ".xlsm", ".xlt", ".xltx", ".ods", ".csv",
        ".ppt", ".pptx", ".pps", ".ppsx", ".odp",
        ".html", ".htm", ".txt",
    ];

    private readonly LibreOfficeOptions _options;
    private readonly ILogger<LibreOfficeConverter> _logger;
    private readonly SemaphoreSlim _pool;
    private readonly ConcurrentProfilePool _profiles;

    /// <summary>Construit le convertisseur et prepare le pool de profils.</summary>
    /// <param name="options">Reglages.</param>
    /// <param name="logger">Journalisation.</param>
    public LibreOfficeConverter(LibreOfficeOptions options, ILogger<LibreOfficeConverter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;
        _pool = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _profiles = new ConcurrentProfilePool(options.ProfileRootDirectory, options.MaxConcurrency);
    }

    /// <inheritdoc />
    public string Name => "LibreOffice";

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        return SupportedExtensions.Contains(extension);
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _pool.WaitAsync(cancellationToken).ConfigureAwait(false);
        var profile = _profiles.Rent();

        try
        {
            return await RunAsync(request, profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profiles.Return(profile);
            _pool.Release();
        }
    }

    private async Task<ConversionResult> RunAsync(
        ConversionRequest request,
        string profileDirectory,
        CancellationToken cancellationToken)
    {
        // Repertoire de sortie dedie a cette conversion : LibreOffice nomme le
        // PDF d'apres le fichier source, deux conversions simultanees de deux
        // "facture.pdf" se telescoperaient dans un repertoire commun.
        var outputDirectory = Path.Combine(request.OutputDirectory, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.SofficePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var profileUri = new Uri(profileDirectory).AbsoluteUri;

        startInfo.ArgumentList.Add($"-env:UserInstallation={profileUri}");
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--norestore");
        startInfo.ArgumentList.Add("--nolockcheck");
        startInfo.ArgumentList.Add("--nodefault");
        startInfo.ArgumentList.Add("--nofirststartwizard");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add(_options.PdfFilter ?? "pdf");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(request.SourcePath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);

            _logger.LogWarning(
                "Conversion abandonnee apres {Timeout} : {LogicalPath}",
                request.Timeout, request.LogicalPath);

            return ConversionResult.Failed(
                Failure.Transient($"Conversion LibreOffice interrompue apres {request.Timeout}."),
                Name);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        // Le code de sortie ne prouve rien : LibreOffice retourne 0 sans avoir
        // ecrit de fichier sur certains documents. Seul le PDF fait foi.
        var produced = Directory.EnumerateFiles(outputDirectory, "*.pdf").FirstOrDefault();

        if (produced is null)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            return ConversionResult.Failed(
                Failure.Permanent(
                    $"LibreOffice n'a produit aucun PDF (code {process.ExitCode}). {error.Trim()}"),
                Name);
        }

        // Deplacement sous un nom stable, derive du chemin logique : le nom
        // choisi par LibreOffice depend du fichier source et n'est pas unique.
        var finalPath = Path.Combine(
            request.OutputDirectory,
            SanitizeSegment(request.LogicalPath) + ".pdf");

        File.Move(produced, finalPath, overwrite: true);
        Directory.Delete(outputDirectory, recursive: true);

        return ConversionResult.Success(finalPath, Name);
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Arbre complet : soffice.exe lance soffice.bin, tuer le parent
                // seul laisse un orphelin qui garde le profil verrouille.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Processus LibreOffice deja termine");
        }
    }

    private static string SanitizeSegment(string logicalPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. logicalPath.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' ? '_' : c)]);

        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pool.Dispose();
        _profiles.Dispose();
    }

    /// <summary>
    /// Distribue des repertoires de profil distincts, un par slot de concurrence.
    /// Les profils sont reutilises entre conversions : les recreer a chaque fois
    /// couterait plusieurs secondes de premiere initialisation LibreOffice.
    /// </summary>
    private sealed class ConcurrentProfilePool : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _available = [];
        private readonly string _root;

        public ConcurrentProfilePool(string rootDirectory, int size)
        {
            _root = rootDirectory;
            Directory.CreateDirectory(rootDirectory);

            for (var i = 0; i < size; i++)
            {
                var path = Path.Combine(rootDirectory, $"profile{i}");
                Directory.CreateDirectory(path);
                _available.Add(path);
            }
        }

        public string Rent() =>
            _available.TryTake(out var profile)
                ? profile
                // Ne devrait pas arriver : le semaphore borne deja les emprunts.
                // Un profil de secours vaut mieux qu'une exception en production.
                : Path.Combine(_root, "profile-fallback-" + Guid.NewGuid().ToString("N")[..8]);

        public void Return(string profile) => _available.Add(profile);

        public void Dispose()
        {
            // Les profils sont conserves entre demarrages : les supprimer
            // imposerait une reinitialisation complete de LibreOffice au
            // premier document suivant, soit plusieurs secondes perdues.
        }
    }
}
