using ACPoller.Abstractions.Conversion;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Infrastructure;
using ACPoller.Abstractions.Metadata;
using ACPoller.Abstractions.Sources;
using ACPoller.Conversion.Converters;
using ACPoller.Conversion.Extraction;
using ACPoller.Conversion.Fonts;
using ACPoller.Conversion.Parsing;
using ACPoller.Core.Configuration;
using ACPoller.Core.Metadata;
using ACPoller.Core.Naming;
using ACPoller.Core.Observability;
using ACPoller.Core.Pipeline;
using ACPoller.Core.Plugins;
using ACPoller.Core.Storage;
using ACPoller.Core.Workers;
using ACPoller.Export.FileSystem;
using ACPoller.Export.Ftp;
using ACPoller.Export.S3;
using ACPoller.Service.Control;
using ACPoller.Service.Security;
using ACPoller.Sources.Folder;
using ACPoller.Sources.Graph;
using ACPoller.Sources.Imap;
using Microsoft.Extensions.Options;
using NLog.Extensions.Logging;

namespace ACPoller.Service;

/// <summary>Point d'entree du service ACPoller.</summary>
public static class Program
{
    private const string LoggerCategory = "ACPoller.Service";

    /// <summary>Demarre l'hote, en service Windows ou en console selon les arguments.</summary>
    /// <param name="args">Arguments de ligne de commande. "--console" force le mode interactif.</param>
    /// <returns>Code de sortie du processus.</returns>
    public static async Task<int> Main(string[] args)
    {
        // Routage EXPLICITE par argument, jamais par Environment.UserInteractive.
        // Ce piege a deja coute un "le service demarre mais ne traite rien" sur
        // ACTxt2Xml : selon le compte de service et la session, UserInteractive
        // ne vaut pas ce qu'on croit, et le demarrage part dans la mauvaise branche.
        var runAsConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

        var builder = WebApplication.CreateBuilder(args);

        // Environnement force : launchSettings.json impose Development en
        // developpement, ce qui active des comportements ASP.NET dont ce
        // service n'a que faire et qui faussent les essais. Un service de
        // capture n'a pas d'environnement de developpement.
        builder.Environment.EnvironmentName = Environments.Production;

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables("ACPOLLER_");

        builder.Logging.ClearProviders();
        builder.Logging.AddNLog();

        if (runAsConsole)
        {
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        }
        else
        {
            builder.Host.UseWindowsService(options => options.ServiceName = "ACPollerForMail");
        }

        ConfigureServices(builder);
        ConfigureKestrel(builder);

        var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        logger.LogInformation(
            "ACPoller demarre (mode {Mode}, version {Version})",
            runAsConsole ? "console" : "service",
            typeof(Program).Assembly.GetName().Version);

        await StartupChecksAsync(app, logger).ConfigureAwait(false);

        // Options resolues APRES le chiffrement des secrets : le jeton est
        // stocke chiffre dans le fichier, il est dechiffre par le singleton.
        var controlOptions = app.Services.GetRequiredService<ControlApiOptions>();

        if (controlOptions.Enabled)
        {
            app.MapControlEndpoints();
            ControlEndpoints.ServiceStartedUtc = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(controlOptions.Token))
            {
                // Sans jeton, tout processus local peut relancer un worker ou
                // lire la configuration. Acceptable en developpement, jamais
                // sur un serveur partage.
                logger.LogWarning(
                    "API de pilotage SANS JETON sur le port {Port}. A ne pas laisser en production.",
                    controlOptions.Port);
            }
            else
            {
                logger.LogInformation(
                    "API de pilotage sur http://127.0.0.1:{Port}/api", controlOptions.Port);
            }
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("ControlApi").Get<ControlApiOptions>()
            ?? new ControlApiOptions();

        // Neutralise les URL imposees par launchSettings.json ou par la
        // variable ASPNETCORE_URLS : l'adresse d'ecoute est une decision de ce
        // code, pas un reglage de poste de developpement.
        builder.WebHost.UseUrls();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (!options.Enabled)
            {
                return;
            }

            // BOUCLE LOCALE UNIQUEMENT. L'UI tourne sur la meme machine que le
            // service, rien ne justifie d'exposer ces commandes sur le reseau,
            // et c'est ce qui rend un simple jeton partage suffisant.
            kestrel.ListenLocalhost(options.Port);
        });
    }

    /// <summary>
    /// Verifications faites AVANT le demarrage des workers. Chacune porte sur un
    /// prerequis dont l'absence ne se manifesterait autrement qu'au premier
    /// message traite, en production.
    /// </summary>
    private static async Task StartupChecksAsync(WebApplication app, ILogger logger)
    {
        // 1. Chiffrement des secrets : un mot de passe saisi en clair par
        // l'exploitant ne doit pas survivre au premier demarrage.
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        await app.Services.GetRequiredService<SettingsProtectionService>()
            .ProtectAsync(settingsPath, CancellationToken.None).ConfigureAwait(false);

        // 2. Polices : sans elles, tout rendu de texte leve une exception
        // PDFsharp incomprehensible au premier document.
        var fonts = app.Services.GetRequiredService<EmbeddedFontResolver>();
        var missingFonts = fonts.Verify();

        if (missingFonts.Count > 0)
        {
            logger.LogWarning("Polices introuvables : {Files}", string.Join(", ", missingFonts));
        }

        fonts.Register();

        // 3. LibreOffice : un chemin errone se voit ici plutot qu'a la
        // premiere piece jointe bureautique.
        var libreOffice = app.Services.GetRequiredService<IOptions<LibreOfficeOptions>>().Value;

        if (!File.Exists(libreOffice.SofficePath))
        {
            logger.LogError(
                "LibreOffice introuvable : {Path}. Les pieces bureautiques ne seront pas converties.",
                libreOffice.SofficePath);
        }

        // 5. Feuilles de style : une feuille absente ou fautive ne se
        // manifesterait autrement qu'a la premiere facture, et produirait des
        // fichiers de sortie inexploitables sans arreter la capture.
        var writers = app.Services.GetServices<IMetadataWriter>();
        var xslt = writers.OfType<XsltMetadataWriter>().FirstOrDefault();

        if (xslt is not null)
        {
            var resolver = app.Services.GetRequiredService<ConfigurationResolver>();
            var root = app.Services.GetRequiredService<IConfiguration>();

            foreach (var config in resolver.Resolve(root).Where(c => c.Enabled))
            {
                var path = config.Output.Metadata.Template;

                if (!string.Equals(config.Output.Metadata.Format, "xslt", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var error = xslt.Validate(path);

                if (error is not null)
                {
                    logger.LogError(
                        "Configuration {Name} : feuille de style invalide ({Path}) : {Error}",
                        config.Name, path, error);
                }
            }
        }

        // Les sauvegardes en clair d'un demarrage precedent sont signalees
        // immediatement : attendre la premiere passe de maintenance laisserait
        // les secrets exposes jusqu'a une heure de plus.
        var settingsDirectory = Path.GetDirectoryName(ConfigurationWriter.SettingsPath);

        if (!string.IsNullOrEmpty(settingsDirectory))
        {
            var clearBackups = Directory
                .EnumerateFiles(settingsDirectory, "*.clear.*.bak")
                .ToArray();

            if (clearBackups.Length > 0)
            {
                logger.LogWarning(
                    "{Count} sauvegarde(s) de configuration EN CLAIR presente(s) dans {Directory}. "
                    + "Elles contiennent les secrets non chiffres et seront purgees a la prochaine "
                    + "passe de maintenance. Les supprimer maintenant si le chiffrement est verifie.",
                    clearBackups.Length,
                    settingsDirectory);
            }
        }

        // 4. Plugins : un plugin incompatible doit se voir au demarrage, pas a
        // la premiere facture. La validation de configuration en depend aussi.
        var plugins = app.Services.GetRequiredService<PluginManager>();
        var options = app.Services.GetRequiredService<IOptions<PollerOptions>>().Value;
        plugins.Load(options.PluginDirectory);
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;

        // ---- Options -------------------------------------------------------
        services.AddOptions<PollerOptions>()
            .Bind(configuration.GetSection(PollerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LibreOfficeOptions>().Bind(configuration.GetSection("Conversion:LibreOffice"));
        services.AddOptions<PdfAssemblerOptions>().Bind(configuration.GetSection("Conversion:Pdf"));
        services.AddOptions<FontResolverOptions>().Bind(configuration.GetSection("Conversion:Fonts"));
        services.AddOptions<TextPdfLayout>().Bind(configuration.GetSection("Conversion:TextLayout"));
        services.AddOptions<CsvMetadataOptions>().Bind(configuration.GetSection("Metadata:Csv"));
        services.AddOptions<GraphThrottlingOptions>().Bind(configuration.GetSection("Graph:Throttling"));
        services.AddOptions<MaintenanceOptions>().Bind(configuration.GetSection("Maintenance"));
        services.AddOptions<ControlApiOptions>().Bind(configuration.GetSection("ControlApi"));
        services.AddOptions<SqlMetricsOptions>().Bind(configuration.GetSection("Metrics:SqlServer"));
        services.AddOptions<SmtpAckOptions>().Bind(configuration.GetSection("Ack:Smtp"));

        // ---- Infrastructure ------------------------------------------------
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsProtectionService>();
        services.AddSingleton<INameTemplateResolver, NameTemplateResolver>();
        services.AddSingleton<GlobalDiskInfo>();

        // Le jeton de pilotage est chiffre dans le fichier comme tout secret :
        // ce singleton expose la version dechiffree, et c'est LUI que le filtre
        // d'authentification consomme. Resoudre IOptions directement comparerait
        // le jeton fourni a une chaine "ENC:...", et refuserait tout.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ControlApiOptions>>().Value;
            var protector = sp.GetRequiredService<ISecretProtector>();

            return options with { Token = protector.Unprotect(options.Token ?? string.Empty) };
        });

        services.AddSingleton<IProcessedStore>(sp => new SqliteProcessedStore(
            Path.Combine(WorkDirectory(sp), "state", "processed.db"),
            sp.GetRequiredService<ILogger<SqliteProcessedStore>>()));

        services.AddSingleton<ICaptureStateStore>(sp => new FileCaptureStateStore(
            WorkDirectory(sp),
            sp.GetRequiredService<ILogger<FileCaptureStateStore>>()));

        // ---- Polices et rendu ----------------------------------------------
        services.AddSingleton(sp => new EmbeddedFontResolver(
            sp.GetRequiredService<IOptions<FontResolverOptions>>().Value,
            sp.GetRequiredService<ILogger<EmbeddedFontResolver>>()));

        services.AddSingleton(sp => new TextPdfRenderer(
            sp.GetRequiredService<EmbeddedFontResolver>(),
            sp.GetRequiredService<IOptions<TextPdfLayout>>().Value));

        services.AddSingleton<ISubstituteDocumentWriter, SubstitutePdfWriter>();

        // ---- Parsing et extraction -----------------------------------------
        services.AddSingleton<IMessageParser, MimeMessageParser>();
        services.AddSingleton<IContainerExtractor, ZipExtractor>();
        services.AddSingleton<IContainerExtractor, TarExtractor>();
        services.AddSingleton<IContainerExtractor, EmlExtractor>();
        services.AddSingleton<IContainerExtractor, MsgExtractor>();

        // ---- Conversion PDF ------------------------------------------------
        services.AddSingleton<IPdfAssembler>(sp => new PdfSharpAssembler(
            sp.GetRequiredService<IOptions<PdfAssemblerOptions>>().Value,
            sp.GetRequiredService<ILogger<PdfSharpAssembler>>()));

        // L'ordre d'enregistrement n'importe pas : la chaine trie par priorite
        // decroissante. PdfPassThrough (1000) prime pour qu'un PDF n'atteigne
        // jamais LibreOffice, qui le reconvertirait en degradant.
        services.AddSingleton<IDocumentConverter, PdfPassThroughConverter>();
        services.AddSingleton<IDocumentConverter, PlainTextConverter>();
        services.AddSingleton<IDocumentConverter, ImageConverter>();
        services.AddSingleton<IDocumentConverter>(sp => new LibreOfficeConverter(
            sp.GetRequiredService<IOptions<LibreOfficeOptions>>().Value,
            sp.GetRequiredService<ILogger<LibreOfficeConverter>>()));

        // ---- Fichiers d'information ----------------------------------------
        services.AddSingleton<IMetadataWriter, JsonMetadataWriter>();
        services.AddSingleton<IMetadataWriter, XmlMetadataWriter>();
        services.AddSingleton<IMetadataWriter, CsvMetadataWriter>();

        // ---- Sources et cibles ---------------------------------------------
        services.AddSingleton(sp => new GraphClientProvider(
            sp.GetRequiredService<IOptions<GraphThrottlingOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ILogger<GraphClientProvider>>()));

        services.AddSingleton<IMailSourceFactory, ImapMailSourceFactory>();
        services.AddSingleton<IMailSourceFactory, GraphMailSourceFactory>();
        services.AddSingleton<IMailSourceFactory, FolderMailSourceFactory>();

        services.AddSingleton<IExportTargetFactory, FileSystemExportTargetFactory>();
        services.AddSingleton<IExportTargetFactory, FtpExportTargetFactory>();
        services.AddSingleton<IExportTargetFactory, S3ExportTargetFactory>();

        services.AddSingleton<RestartTracker>();

        // ---- Observabilite -------------------------------------------------
        // Fournisseur reel, enregistre sous son propre type.
        if (string.IsNullOrWhiteSpace(configuration["Metrics:SqlServer:ConnectionString"]))
        {
            services.AddSingleton<NullMetricsProvider>();
            services.AddSingleton(sp => new MetricsSnapshotProvider(
                sp.GetRequiredService<NullMetricsProvider>()));
        }
        else
        {
            services.AddSingleton(sp => new SqlServerMetricsProvider(
                sp.GetRequiredService<IOptions<SqlMetricsOptions>>().Value,
                sp.GetRequiredService<ILogger<SqlServerMetricsProvider>>()));
            services.AddHostedService(sp => sp.GetRequiredService<SqlServerMetricsProvider>());
            services.AddSingleton(sp => new MetricsSnapshotProvider(
                sp.GetRequiredService<SqlServerMetricsProvider>()));
        }

        // Tout le socle passe par le decorateur : c'est lui qui alimente le
        // tableau de bord, et il delegue au fournisseur reel.
        services.AddSingleton<IMetricsProvider>(sp => sp.GetRequiredService<MetricsSnapshotProvider>());

        if (string.IsNullOrWhiteSpace(configuration["Ack:Smtp:Host"]))
        {
            services.AddSingleton<IAckSender, NullAckSender>();
        }
        else
        {
            services.AddSingleton<IAckSender>(sp => new SmtpAckSender(
                sp.GetRequiredService<IOptions<SmtpAckOptions>>().Value,
                sp.GetRequiredService<ILogger<SmtpAckSender>>()));
        }

        // ---- Plugins et configuration --------------------------------------
        services.AddSingleton<PluginDiscovery>();
        services.AddSingleton<PluginManager>();
        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<ConfigurationValidator>();

        // ---- Pipeline ------------------------------------------------------
        services.AddSingleton<DocumentTreeBuilder>();
        services.AddSingleton<ConverterChain>();
        services.AddSingleton<ExportCoordinator>();
        services.AddSingleton<CapturePipeline>();

        // ---- Workers et maintenance ----------------------------------------
        // Le double enregistrement est volontaire : l'API de pilotage doit
        // recuperer LA MEME instance que l'hote pour superviser et redemarrer
        // un worker. Un AddHostedService seul en creerait une seconde.
        services.AddRuntimeFactory();
        services.AddSingleton<PollerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PollerHostedService>());

        services.AddSingleton<MaintenanceService>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());

        services.AddSingleton<ConfigurationWriter>();
        services.AddSingleton<MetadataFieldResolver>();
        services.AddSingleton<IMetadataWriter, XsltMetadataWriter>();
    }

    private static string WorkDirectory(IServiceProvider services) =>
        services.GetRequiredService<IOptions<PollerOptions>>().Value.WorkDirectory;
}
