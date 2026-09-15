using System.Text.Json.Nodes;
using ACPoller.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace ACPoller.Core.Tests;

/// <summary>
/// Tests de l'ecriture de configuration.
/// </summary>
/// <remarks>
/// Ce composant ecrit dans le fichier de PRODUCTION d'un service en cours
/// d'execution. Trois defauts y seraient couteux et silencieux : ecraser la
/// modification d'un collegue, effacer un secret que l'interface ne renvoie
/// pas, et perdre les reglages que le formulaire ne connait pas.
/// </remarks>
public sealed class ConfigurationWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "acpoller-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigurationWriterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Un_secret_vide_conserve_la_valeur_existante()
    {
        // L'interface ne recoit JAMAIS les secrets : elle renvoie donc des
        // champs vides. Sans ce report, chaque enregistrement depuis l'ecran
        // effacerait le mot de passe, et la panne n'apparaitrait qu'au cycle
        // suivant.
        var existing = new JsonObject
        {
            ["Name"] = "BOITE",
            ["Source"] = new JsonObject
            {
                ["Mailbox"] = "a@b.fr",
                ["Password"] = "ENC:valeur-chiffree",
            },
        };

        var payload = new JsonObject
        {
            ["Name"] = "BOITE",
            ["Source"] = new JsonObject
            {
                ["Mailbox"] = "nouveau@b.fr",
                ["Password"] = string.Empty,
            },
        };

        InvokePreserveSecrets(existing, payload);

        payload["Source"]!["Password"]!.GetValue<string>().ShouldBe("ENC:valeur-chiffree");
        payload["Source"]!["Mailbox"]!.GetValue<string>().ShouldBe("nouveau@b.fr");
    }

    [Fact]
    public void Un_secret_renseigne_remplace_la_valeur_existante()
    {
        var existing = new JsonObject
        {
            ["Source"] = new JsonObject { ["ClientSecret"] = "ENC:ancien" },
        };

        var payload = new JsonObject
        {
            ["Source"] = new JsonObject { ["ClientSecret"] = "nouveau-secret" },
        };

        InvokePreserveSecrets(existing, payload);

        payload["Source"]!["ClientSecret"]!.GetValue<string>().ShouldBe("nouveau-secret");
    }

    [Fact]
    public void Un_reglage_inconnu_du_formulaire_survit_a_l_enregistrement()
    {
        // Le formulaire n'ecrit que les noeuds qu'il connait. Un reglage
        // ajoute par une future version, ou saisi a la main, doit traverser.
        var existing = new JsonObject
        {
            ["Source"] = new JsonObject
            {
                ["Mailbox"] = "a@b.fr",
                ["ReglageFutur"] = "valeur-a-conserver",
            },
        };

        var payload = existing.DeepClone().AsObject();
        payload["Source"]!["Mailbox"] = "modifie@b.fr";

        payload["Source"]!["ReglageFutur"]!.GetValue<string>().ShouldBe("valeur-a-conserver");
    }

    [Fact]
    public void L_empreinte_change_quand_le_fichier_change()
    {
        var path = Path.Combine(_directory, "settings.json");

        File.WriteAllText(path, """{ "Poller": { "MaxGlobalConcurrency": 2 } }""");
        var first = ComputeVersion(path);

        File.WriteAllText(path, """{ "Poller": { "MaxGlobalConcurrency": 4 } }""");
        var second = ComputeVersion(path);

        first.ShouldNotBe(second);
    }

    [Fact]
    public void L_empreinte_est_stable_sur_un_fichier_inchange()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "Poller": {} }""");

        ComputeVersion(path).ShouldBe(ComputeVersion(path));
    }

    [Fact]
    public void Un_reglage_de_service_exige_un_redemarrage()
    {
        RestartTracker.GetRestartReason("Poller:MaxGlobalConcurrency").ShouldNotBeNull();
        RestartTracker.GetRestartReason("ControlApi:Port").ShouldNotBeNull();
        RestartTracker.GetRestartReason("Conversion:Fonts:FontDirectory").ShouldNotBeNull();
    }

    [Fact]
    public void Un_reglage_de_boite_n_exige_pas_de_redemarrage()
    {
        // Les configurations sont rechargeables par redemarrage de worker :
        // les marquer ferait clignoter l'alerte a chaque enregistrement, et
        // elle serait ignoree en deux jours.
        RestartTracker.GetRestartReason("Poller:Configurations").ShouldBeNull();
        RestartTracker.GetRestartReason("Poller:Templates").ShouldBeNull();
    }

    [Fact]
    public void Le_suivi_de_redemarrage_se_vide()
    {
        var tracker = new RestartTracker(NullLogger<RestartTracker>.Instance);

        tracker.Mark("Poller:MaxGlobalConcurrency");
        tracker.IsRestartRequired.ShouldBeTrue();

        tracker.Clear();
        tracker.IsRestartRequired.ShouldBeFalse();
    }

    [Fact]
    public void Marquer_un_reglage_rechargeable_ne_declenche_rien()
    {
        var tracker = new RestartTracker(NullLogger<RestartTracker>.Instance);

        tracker.Mark("Poller:Configurations");

        tracker.IsRestartRequired.ShouldBeFalse();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string ComputeVersion(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))[..16];
    }

    /// <summary>
    /// Appelle la methode privee de report des secrets.
    /// </summary>
    /// <remarks>
    /// Reflexion assumee : cette regle est le point le plus risque du writer,
    /// et la rendre publique pour la tester exposerait une mecanique interne
    /// dans le contrat.
    /// </remarks>
    private static void InvokePreserveSecrets(JsonObject existing, JsonObject payload)
    {
        var method = typeof(ConfigurationWriter).GetMethod(
            "PreserveSecrets",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.ShouldNotBeNull("PreserveSecrets a ete renomme ou n'est plus statique.");
        method!.Invoke(null, [existing, payload]);
    }
}
