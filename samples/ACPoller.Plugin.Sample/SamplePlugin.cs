using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Plugins;
using ACPoller.Abstractions.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: AcPollerPlugin(ContractVersion.Current, typeof(ACPoller.Plugin.Sample.SamplePlugin))]

namespace ACPoller.Plugin.Sample;

/// <summary>
/// Squelette d'un plugin metier client (equivalent d'ACPFacturesAssembly).
/// Le loader lit l'attribut d'assemblage, verifie la version de contrat, puis
/// instancie ce type. Aucune reflexion sur une signature de methode.
/// </summary>
public sealed class SamplePlugin : IAcPollerPlugin
{
    public string Name => "Sample";

    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ICaptureProcessor, SubjectRoutingProcessor>();
    }
}

/// <summary>
/// Exemple de traitement : extraction d'un code fournisseur depuis le sujet,
/// et rejet typE si absent. Illustre le contrat, la logique reelle est cote client.
/// </summary>
public sealed class SubjectRoutingProcessor : ICaptureProcessor
{
    public string Name => "SubjectRouting";

    public IReadOnlyList<ProcessingStage> Stages => [ProcessingStage.AfterParse];

    public int Order => 100;

    public Task<ProcessingOutcome> ProcessAsync(
        Abstractions.Model.CaptureContext context,
        ProcessingStage stage,
        CancellationToken cancellationToken)
    {
        var subject = context.Envelope.Subject;

        if (string.IsNullOrWhiteSpace(subject))
        {
            // Rejet metier : declenche la notification, pas une exception.
            return Task.FromResult(ProcessingOutcome.Reject("R001", "Sujet vide"));
        }

        // Les donnees extraites transitent par le sac de proprietes, jamais
        // par une ecriture directe dans le fichier d'information.
        context.Properties["Supplier"] = subject.Split('-')[0].Trim();

        return Task.FromResult(ProcessingOutcome.Continue());
    }
}
