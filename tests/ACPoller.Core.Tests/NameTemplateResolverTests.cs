using ACPoller.Abstractions.Model;
using ACPoller.Core.Naming;
using Shouldly;
using Xunit;

namespace ACPoller.Core.Tests;

/// <summary>
/// Tests du moteur de nommage.
/// </summary>
/// <remarks>
/// Un defaut ici ne provoque aucune erreur : il produit des fichiers qui
/// s'ecrasent entre eux. C'est exactement ce qui est arrive en mode Both, ou
/// le PDF unifie et la premiere piece portaient le meme nom, la seconde
/// ecrasant la premiere a l'export sans le moindre avertissement.
/// </remarks>
public sealed class NameTemplateResolverTests
{
    private static readonly DateTimeOffset Reception =
        new(2026, 3, 14, 9, 15, 0, TimeSpan.Zero);

    /// <summary>
    /// Construit un contexte complet.
    /// </summary>
    /// <remarks>
    /// Le sujet est passe a la CONSTRUCTION et non modifie apres coup :
    /// MessageEnvelope est immuable, et le contourner dans un test reviendrait
    /// a tester autre chose que ce que le code de production manipule.
    /// </remarks>
    private static CaptureContext CreateContext(string? subject = "Facture 2026-4471")
    {
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            From = "fournisseur@exemple.fr",
            ReceivedUtc = Reception,
        };

        return new CaptureContext
        {
            CorrelationId = "TEST_abc123",
            MailboxId = "factures@client.fr",
            ConfigurationName = "FACTURES",
            Identity = new MessageIdentity
            {
                ProviderId = "msg-001",
                InternetMessageId = "<msg@exemple.fr>",
                DeduplicationKey = "<msg@exemple.fr>",
            },
            Envelope = envelope,
            Root = new DocumentNode("message", DocumentKind.EmbeddedMessage, "message"),
            WorkDirectory = Path.Combine(Path.GetTempPath(), "acpoller-test"),
        };
    }

    private static NameTemplateResolver CreateResolver() => new();

    [Fact]
    public void Le_pdf_unifie_et_la_premiere_piece_ne_portent_pas_le_meme_nom()
    {
        // Le defaut trouve en production : {index} valait 000 pour le document
        // de niveau message comme pour la piece d'indice 0, et l'export
        // ecrasait l'un par l'autre.
        var resolver = CreateResolver();
        var context = CreateContext();

        var merged = resolver.Resolve("{subject}_{index}", context, node: null, index: null);
        var first = resolver.Resolve("{subject}_{index}", context, node: null, index: 0);

        merged.ShouldNotBe(first);
    }

    [Fact]
    public void Jeton_index_absent_ne_laisse_pas_de_separateur_orphelin()
    {
        var resolver = CreateResolver();

        var name = resolver.Resolve("{subject}_{index}", CreateContext(), node: null, index: null);

        name.ShouldNotEndWith("_");
        name.ShouldNotContain("__");
    }

    [Fact]
    public void Les_pieces_recoivent_des_indices_distincts()
    {
        var resolver = CreateResolver();
        var context = CreateContext();

        var names = Enumerable.Range(0, 3)
            .Select(i => resolver.Resolve("{subject}_{index}", context, node: null, index: i))
            .ToArray();

        names.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Le_format_de_date_est_applique()
    {
        var resolver = CreateResolver();

        var name = resolver.Resolve("{received:yyyyMMdd}", CreateContext(), node: null, index: null);

        name.ShouldBe("20260314");
    }

    [Fact]
    public void Un_sujet_hostile_ne_peut_pas_sortir_du_repertoire()
    {
        var resolver = CreateResolver();
        var context = CreateContext(@"..\..\windows\system32\evil");

        var name = resolver.Resolve("{subject}", context, node: null, index: null);

        name.ShouldNotContain("..");
        name.ShouldNotContain("\\");
        name.ShouldNotContain("/");
    }

    [Fact]
    public void Un_sujet_vide_produit_un_nom_utilisable()
    {
        var resolver = CreateResolver();
        var context = CreateContext(string.Empty);

        var name = resolver.Resolve("{subject}", context, node: null, index: null);

        name.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Un_sujet_absent_produit_un_nom_utilisable()
    {
        // Subject est nullable : un message sans objet est parfaitement
        // legitime, et le corpus en contient un exemplaire.
        var resolver = CreateResolver();
        var context = CreateContext(subject: null);

        var name = resolver.Resolve("{subject}", context, node: null, index: null);

        name.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Un_jeton_inconnu_ne_fait_pas_echouer_le_nommage()
    {
        // Une faute de frappe dans le gabarit ne doit pas perdre le message :
        // c'est un defaut de configuration, pas une raison de rejeter un
        // document.
        var resolver = CreateResolver();

        Should.NotThrow(() =>
            resolver.Resolve("{inconnu}_{subject}", CreateContext(), node: null, index: null));
    }

    [Fact]
    public void Le_nom_produit_reste_borne_en_longueur()
    {
        var resolver = CreateResolver();
        var context = CreateContext(new string('a', 500));

        var name = resolver.Resolve("{subject}", context, node: null, index: null);

        // Le chemin complet doit tenir sous la limite Windows, repertoire de
        // depot compris : un nom de 500 caracteres la franchit a lui seul.
        name.Length.ShouldBeLessThanOrEqualTo(150);
    }

    [Fact]
    public void La_boite_est_resolue_depuis_le_contexte()
    {
        var resolver = CreateResolver();

        var name = resolver.Resolve("{mailbox}", CreateContext(), node: null, index: null);

        name.ShouldNotBeNullOrWhiteSpace();
        name.ShouldNotContain("@");
    }
}
