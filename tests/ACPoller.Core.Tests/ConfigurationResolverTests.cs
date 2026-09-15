using ACPoller.Abstractions.Infrastructure;

using ACPoller.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace ACPoller.Core.Tests;

/// <summary>
/// Tests de l'heritage par gabarit.
/// </summary>
/// <remarks>
/// C'est la mecanique la plus subtile du socle, et celle dont un defaut serait
/// le plus silencieux : une valeur mal heritee ne provoque aucune erreur, elle
/// produit un comportement different de celui attendu, sur des dizaines de
/// boites a la fois.
/// </remarks>
public sealed class ConfigurationResolverTests
{
    private static ConfigurationResolver CreateResolver() =>
        new(new PassthroughSecretProtector(), NullLogger<ConfigurationResolver>.Instance);

    private static IConfiguration Build(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void Configuration_sans_gabarit_est_resolue_telle_quelle()
    {
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Configurations": [
                  {
                    "Name": "SIMPLE",
                    "Enabled": true,
                    "Source": { "Protocol": "imap", "Mailbox": "a@b.fr" },
                    "Schedule": { "IntervalSeconds": 300 }
                  }
                ]
              }
            }
            """);

        var resolved = resolver.Resolve(configuration).ToArray();

        resolved.Length.ShouldBe(1);
        resolved[0].Name.ShouldBe("SIMPLE");
        resolved[0].Source.Mailbox.ShouldBe("a@b.fr");
        resolved[0].Schedule.IntervalSeconds.ShouldBe(300);
    }

    [Fact]
    public void Valeurs_du_gabarit_sont_heritees()
    {
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Templates": {
                  "standard": {
                    "Source": {
                      "Protocol": "graph",
                      "TenantId": "tenant-commun",
                      "ClientId": "client-commun"
                    },
                    "Schedule": { "IntervalSeconds": 120 }
                  }
                },
                "Configurations": [
                  {
                    "Name": "BOITE-01",
                    "Template": "standard",
                    "Source": { "Mailbox": "boite01@client.fr" }
                  }
                ]
              }
            }
            """);

        var resolved = resolver.Resolve(configuration).Single();

        resolved.Source.TenantId.ShouldBe("tenant-commun");
        resolved.Source.ClientId.ShouldBe("client-commun");
        resolved.Source.Protocol.ShouldBe("graph");
        resolved.Schedule.IntervalSeconds.ShouldBe(120);

        // Ce qui est propre a la configuration ne doit pas avoir ete perdu.
        resolved.Source.Mailbox.ShouldBe("boite01@client.fr");
    }

    [Fact]
    public void Configuration_surcharge_le_gabarit()
    {
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Templates": {
                  "standard": { "Schedule": { "IntervalSeconds": 120 } }
                },
                "Configurations": [
                  {
                    "Name": "LENTE",
                    "Template": "standard",
                    "Source": { "Protocol": "imap", "Mailbox": "a@b.fr" },
                    "Schedule": { "IntervalSeconds": 3600 }
                  }
                ]
              }
            }
            """);

        resolver.Resolve(configuration).Single().Schedule.IntervalSeconds.ShouldBe(3600);
    }

    [Fact]
    public void Le_nom_de_la_configuration_survit_a_l_heritage()
    {
        // Un gabarit portant un Name le transmettrait a toutes ses heritieres,
        // qui porteraient alors le meme identifiant : le journal des messages
        // traites serait partage entre des boites distinctes.
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Templates": {
                  "fautif": {
                    "Name": "NOM-DU-GABARIT",
                    "Source": { "Protocol": "imap" }
                  }
                },
                "Configurations": [
                  { "Name": "VRAI-NOM", "Template": "fautif", "Source": { "Mailbox": "a@b.fr" } }
                ]
              }
            }
            """);

        resolver.Resolve(configuration).Single().Name.ShouldBe("VRAI-NOM");
    }

    [Fact]
    public void Liste_redeclaree_remplace_celle_du_gabarit()
    {
        // Regle structurante : les listes sont REMPLACEES, jamais fusionnees.
        // Une fusion par indice produirait un melange incomprehensible, ou la
        // premiere cible du gabarit se retrouverait ecrasee par la premiere de
        // la configuration tout en gardant les suivantes.
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Templates": {
                  "deux-cibles": {
                    "Output": {
                      "Targets": [
                        { "Type": "fs", "Name": "a", "Settings": { "Path": "C:\\a" } },
                        { "Type": "fs", "Name": "b", "Settings": { "Path": "C:\\b" } }
                      ]
                    }
                  }
                },
                "Configurations": [
                  {
                    "Name": "UNE-SEULE",
                    "Template": "deux-cibles",
                    "Source": { "Protocol": "imap", "Mailbox": "a@b.fr" },
                    "Output": {
                      "Targets": [
                        { "Type": "fs", "Name": "c", "Settings": { "Path": "C:\\c" } }
                      ]
                    }
                  }
                ]
              }
            }
            """);

        var targets = resolver.Resolve(configuration).Single().Output.Targets;

        targets.Count.ShouldBe(1);
        targets[0].Name.ShouldBe("c");
    }

    [Fact]
    public void Gabarit_introuvable_ecarte_la_configuration()
    {
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Configurations": [
                  { "Name": "ORPHELINE", "Template": "inexistant", "Source": { "Mailbox": "a@b.fr" } }
                ]
              }
            }
            """);

        // Ecarter et non lever : une configuration fautive ne doit pas
        // empecher les quatre-vingt-dix-neuf autres de tourner.
        Should.NotThrow(() => resolver.Resolve(configuration).ToArray());
    }

    [Fact]
    public void Plusieurs_configurations_heritent_du_meme_gabarit_sans_se_contaminer()
    {
        // Le piege du binder : lier deux configurations sur le MEME objet de
        // gabarit ferait que la seconde verrait les valeurs de la premiere.
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Templates": {
                  "commun": { "Source": { "Protocol": "graph", "TenantId": "t1" } }
                },
                "Configurations": [
                  { "Name": "A", "Template": "commun", "Source": { "Mailbox": "a@client.fr" } },
                  { "Name": "B", "Template": "commun", "Source": { "Mailbox": "b@client.fr" } }
                ]
              }
            }
            """);

        var resolved = resolver.Resolve(configuration).ToArray();

        resolved.Length.ShouldBe(2);
        resolved[0].Source.Mailbox.ShouldBe("a@client.fr");
        resolved[1].Source.Mailbox.ShouldBe("b@client.fr");

        resolved.ShouldAllBe(c => c.Source.TenantId == "t1");
    }

    [Fact]
    public void Configuration_desactivee_est_resolue_mais_marquee()
    {
        // Elle doit apparaitre dans la liste : l'ecran de configuration doit
        // pouvoir la montrer et la reactiver. C'est le demarrage des workers
        // qui filtre sur Enabled, pas la resolution.
        var resolver = CreateResolver();

        var configuration = Build("""
            {
              "Poller": {
                "Configurations": [
                  {
                    "Name": "SUSPENDUE",
                    "Enabled": false,
                    "Source": { "Protocol": "imap", "Mailbox": "a@b.fr" }
                  }
                ]
              }
            }
            """);

        var resolved = resolver.Resolve(configuration).Single();

        resolved.Name.ShouldBe("SUSPENDUE");
        resolved.Enabled.ShouldBeFalse();
    }
}

/// <summary>
/// Protecteur neutre pour les tests : rend les valeurs telles quelles.
/// </summary>
/// <remarks>
/// DPAPI est lie a la machine et au compte. L'utiliser en test rendrait les
/// resultats dependants de l'environnement d'execution, ce qui est exactement
/// ce qu'un test ne doit pas etre.
/// </remarks>
internal sealed class PassthroughSecretProtector : ISecretProtector
{
    /// <inheritdoc />
    public string Protect(string value) => value;

    /// <inheritdoc />
    public string Unprotect(string value) => value;

    /// <inheritdoc />
    public bool IsProtected(string value) =>
        value.StartsWith("ENC:", StringComparison.Ordinal);
}
