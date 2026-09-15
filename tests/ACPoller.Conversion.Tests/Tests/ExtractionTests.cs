using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Model;
using ACPoller.Conversion.Extraction;
using ACPoller.Conversion.Parsing;
using ACPoller.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace ACPoller.Conversion.Tests;

/// <summary>
/// Exerce les garde-fous d'extraction sur des archives hostiles.
/// </summary>
/// <remarks>
/// Ces tests portent sur des attaques, pas sur des cas metier. Ils doivent
/// rester verts meme si la logique d'extraction est reecrite : ce sont eux qui
/// garantissent qu'un mail forge ne peut ni ecrire hors du repertoire de
/// travail, ni saturer le disque, ni figer un worker.
/// </remarks>
public sealed class ExtractionTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "acpoller-tests", Guid.NewGuid().ToString("N"));

    private readonly MimeMessageParser _parser = new(NullLogger<MimeMessageParser>.Instance);

    private static readonly MessageEnvelope Envelope = new()
    {
        Subject = "test",
        From = "expediteur@exemple.fr",
        ReceivedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Zip_slip_est_refuse()
    {
        // Une entree nommee "../../../evil.txt" ne doit jamais sortir du
        // repertoire cible. Verification sur le chemin RESOLU.
        var (context, zipNode, target) = await PrepareAsync("07-zip-slip.eml");

        var extractor = new ZipExtractor(NullLogger<ZipExtractor>.Instance);
        await extractor.ExtractAsync(zipNode, target, new ExtractionLimits(), TestContext.Current.CancellationToken);

        var root = Path.GetFullPath(context.WorkDirectory);

        foreach (var child in zipNode.Children.Where(c => c.SourcePath is not null))
        {
            Path.GetFullPath(child.SourcePath!).ShouldStartWith(root, Case.Insensitive);
        }

        // L'entree legitime passe, la traversante est aplatie ou ecartee.
        zipNode.Children.ShouldContain(c => c.FileName == "legitime.txt");
        Directory.GetParent(root)!.EnumerateFiles("evil.txt").ShouldBeEmpty();
    }

    [Fact]
    public async Task Taux_de_compression_anormal_est_refuse()
    {
        // Vingt mega-octets de zeros dans quelques kilo-octets : le taux
        // depasse largement 200:1, l'entree ne doit pas etre extraite.
        var (_, zipNode, target) = await PrepareAsync("08-bombe.eml");

        var extractor = new ZipExtractor(NullLogger<ZipExtractor>.Instance);
        await extractor.ExtractAsync(zipNode, target, new ExtractionLimits(), TestContext.Current.CancellationToken);

        zipNode.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task Volume_decompresse_est_borne()
    {
        var (_, zipNode, target) = await PrepareAsync("06-zip-dans-zip.eml");

        // Borne volontairement absurde : rien ne doit passer.
        var limits = new ExtractionLimits { MaxExpandedBytes = 10 };

        var extractor = new ZipExtractor(NullLogger<ZipExtractor>.Instance);
        await extractor.ExtractAsync(zipNode, target, limits, TestContext.Current.CancellationToken);

        zipNode.Children.Sum(c => c.SizeBytes).ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task Archive_imbriquee_est_exposee_comme_conteneur()
    {
        // Le zip interne doit apparaitre comme un noeud Archive, pour que le
        // builder le rappelle au tour suivant sous les memes bornes.
        var (_, zipNode, target) = await PrepareAsync("06-zip-dans-zip.eml");

        var extractor = new ZipExtractor(NullLogger<ZipExtractor>.Instance);
        await extractor.ExtractAsync(zipNode, target, new ExtractionLimits(), TestContext.Current.CancellationToken);

        zipNode.Children.ShouldContain(c => c.Kind == DocumentKind.Archive && c.FileName == "dedans.zip");
    }

    [Fact]
    public async Task Chemin_logique_des_enfants_est_prefixe_par_le_conteneur()
    {
        var (_, zipNode, target) = await PrepareAsync("06-zip-dans-zip.eml");

        var extractor = new ZipExtractor(NullLogger<ZipExtractor>.Instance);
        await extractor.ExtractAsync(zipNode, target, new ExtractionLimits(), TestContext.Current.CancellationToken);

        zipNode.Children.ShouldAllBe(c => c.LogicalPath.StartsWith(zipNode.LogicalPath + "/"));
    }

    [Fact]
    public async Task Profondeur_de_recursion_est_bornee()
    {
        // Trois niveaux d'eml imbriques, borne a deux : le troisieme est
        // marque Excluded et non developpe.
        var context = await BuildContextAsync("05-eml-triple.eml");

        var builder = new DocumentTreeBuilder(
            [new EmlExtractor(_parser, NullLogger<EmlExtractor>.Instance)],
            NullLogger<DocumentTreeBuilder>.Instance);

        await builder.ExpandAsync(
            context,
            new ExtractionLimits { MaxDepth = 2 },
            TestContext.Current.CancellationToken);

        context.Root.Walk().ShouldAllBe(n => n.Depth <= 3);
        context.Warnings.ShouldContain(w => w.Reason.Contains("Profondeur"));
    }

    [Fact]
    public async Task Message_imbrique_est_developpe_comme_un_message_recu()
    {
        // Un transfert doit produire exactement la meme structure qu'un envoi
        // direct : meme corps, memes pieces.
        var context = await BuildContextAsync("04-eml-imbrique.eml");

        var builder = new DocumentTreeBuilder(
            [new EmlExtractor(_parser, NullLogger<EmlExtractor>.Instance)],
            NullLogger<DocumentTreeBuilder>.Instance);

        await builder.ExpandAsync(context, new ExtractionLimits(), TestContext.Current.CancellationToken);

        var nested = context.Root.Children.Single(n => n.Kind == DocumentKind.EmbeddedMessage);

        nested.Status.ShouldBe(NodeStatus.Container);
        nested.Children.ShouldContain(c => c.Kind == DocumentKind.Body);
        nested.Children.ShouldContain(c => c.FileName == "bl.pdf");
    }

    private async Task<(CaptureContext Context, DocumentNode ZipNode, string Target)> PrepareAsync(string file)
    {
        var context = await BuildContextAsync(file);

        var zipNode = context.Root.Children.Single(n =>
            n.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        var target = Path.Combine(context.WorkDirectory, "expanded", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);

        return (context, zipNode, target);
    }

    private async Task<CaptureContext> BuildContextAsync(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", file);
        File.Exists(path).ShouldBeTrue($"Fichier de corpus absent : {path}");

        var directory = Path.Combine(_workDirectory, Path.GetFileNameWithoutExtension(file));
        Directory.CreateDirectory(directory);

        await using var stream = File.OpenRead(path);

        var root = await _parser.ParseAsync(
            stream, directory, Envelope, string.Empty, TestContext.Current.CancellationToken);

        return new CaptureContext
        {
            CorrelationId = Path.GetFileNameWithoutExtension(file),
            MailboxId = "test@exemple.fr",
            ConfigurationName = "TEST",
            Identity = new MessageIdentity { ProviderId = "1", DeduplicationKey = "1" },
            Envelope = Envelope,
            Root = root,
            WorkDirectory = directory,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }
}
