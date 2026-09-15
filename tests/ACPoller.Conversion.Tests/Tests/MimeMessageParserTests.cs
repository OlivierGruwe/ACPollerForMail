using ACPoller.Abstractions.Model;
using ACPoller.Conversion.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace ACPoller.Conversion.Tests;

/// <summary>
/// Exerce le parseur sur des messages reels et atypiques.
/// </summary>
/// <remarks>
/// Chaque fichier du corpus correspond a un mode de defaillance observe ou
/// redoute. Le corpus est la vraie valeur de ces tests : il grossit a chaque
/// incident de production, et un incident deja represente ne peut plus revenir.
/// </remarks>
public sealed class MimeMessageParserTests : IDisposable
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
    public async Task Message_sans_partie_texte_ne_produit_pas_de_noeud_de_corps()
    {
        // Le NullReference de production v1 : le corps n'etait cree que dans
        // la branche texte, et un mail sans partie texte plantait le parsing.
        var root = await ParseAsync("01-sans-corps.eml");

        root.Children.ShouldNotBeEmpty();
        root.Children.ShouldNotContain(n => n.Kind == DocumentKind.Body);
        root.Children.ShouldContain(n => n.FileName == "facture.pdf");
    }

    [Fact]
    public async Task Message_html_seul_produit_un_corps_html()
    {
        var root = await ParseAsync("02-html-seul.eml");

        var body = root.Children.Single(n => n.Kind == DocumentKind.Body);
        body.FileName.ShouldBe("corps.html");
        body.ContentType.ShouldBe("text/html");
        File.Exists(body.SourcePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Pieces_de_meme_nom_sont_desambiguisees()
    {
        // Sans desambiguisation, la seconde ecrase la premiere en silence.
        var root = await ParseAsync("03-noms-doublon.eml");

        var attachments = root.Children
            .Where(n => n.Kind == DocumentKind.Attachment)
            .ToArray();

        attachments.Length.ShouldBe(2);
        attachments.Select(a => a.SourcePath).Distinct().Count().ShouldBe(2);
        attachments.Select(a => a.LogicalPath).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Message_imbrique_est_ecrit_sans_etre_developpe()
    {
        // Le developpement revient a l'extracteur, pour que les garde-fous de
        // recursion s'appliquent uniformement.
        var root = await ParseAsync("04-eml-imbrique.eml");

        var nested = root.Children.Single(n => n.Kind == DocumentKind.EmbeddedMessage);
        nested.Children.ShouldBeEmpty();
        File.Exists(nested.SourcePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Piece_sans_nom_recoit_une_extension_deduite_du_type()
    {
        // Un fichier sans extension n'est reconnu par aucun convertisseur.
        var root = await ParseAsync("09-piece-sans-nom.eml");

        var image = root.Children.Single(n =>
            n.Kind is DocumentKind.Attachment or DocumentKind.InlineImage);

        Path.GetExtension(image.FileName).ShouldBe(".png");
    }

    [Fact]
    public async Task Nom_de_fichier_tres_long_est_tronque()
    {
        // Un nom de 300 caracteres fait echouer la creation du fichier sur
        // Windows, d'autant que le repertoire de travail est deja profond.
        var root = await ParseAsync("10-nom-long.eml");

        var attachment = root.Children.Single(n => n.Kind == DocumentKind.Attachment);

        attachment.FileName.Length.ShouldBeLessThanOrEqualTo(100);
        Path.GetExtension(attachment.FileName).ShouldBe(".pdf");
        File.Exists(attachment.SourcePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Nom_de_fichier_hostile_est_assaini_et_reste_dans_le_repertoire()
    {
        // Un nom contenant des separateurs ne doit jamais permettre d'ecrire
        // hors du repertoire de travail.
        var root = await ParseAsync("14-nom-hostile.eml");

        var attachment = root.Children.Single(n => n.Kind == DocumentKind.Attachment);

        attachment.FileName.ShouldNotContain("..");
        attachment.FileName.ShouldNotContain("\\");
        attachment.FileName.ShouldNotContain(":");

        Path.GetFullPath(attachment.SourcePath!)
            .ShouldStartWith(Path.GetFullPath(_workDirectory), Case.Insensitive);
    }

    [Fact]
    public async Task Piece_vide_est_conservee_avec_une_taille_nulle()
    {
        // Une piece de zero octet n'est pas une erreur : elle doit apparaitre
        // dans l'arbre pour que le fichier d'information la mentionne.
        var root = await ParseAsync("16-piece-vide.eml");

        var attachment = root.Children.Single(n => n.Kind == DocumentKind.Attachment);
        attachment.SizeBytes.ShouldBe(0);
        File.Exists(attachment.SourcePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Image_inline_est_classee_distinctement_de_la_piece_jointe()
    {
        // Le logo de signature ne doit pas produire une page dans le PDF.
        var root = await ParseAsync("13-image-inline.eml");

        root.Children.ShouldContain(n => n.Kind == DocumentKind.InlineImage);
        root.Children.ShouldContain(n => n.FileName == "reel.pdf");
    }

    [Fact]
    public async Task Le_prefixe_logique_est_applique_a_tous_les_noeuds()
    {
        // Sans prefixe, deux "corps.html" a deux niveaux d'imbrication
        // porteraient la meme cle de reprise.
        var root = await ParseAsync("02-html-seul.eml", "transfert.eml/");

        root.Children.ShouldAllBe(n => n.LogicalPath.StartsWith("transfert.eml/"));
    }

    [Fact]
    public async Task Chaque_noeud_porte_une_empreinte()
    {
        var root = await ParseAsync("03-noms-doublon.eml");

        root.Children.ShouldAllBe(n => n.Sha256 != null && n.Sha256.Length == 64);
    }

    private async Task<DocumentNode> ParseAsync(string corpusFile, string prefix = "")
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", corpusFile);
        File.Exists(path).ShouldBeTrue($"Fichier de corpus absent : {path}");

        var directory = Path.Combine(_workDirectory, Path.GetFileNameWithoutExtension(corpusFile));
        Directory.CreateDirectory(directory);

        await using var stream = File.OpenRead(path);
        return await _parser.ParseAsync(stream, directory, Envelope, prefix, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }
}
