using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;
using ACPoller.Abstractions.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace ACPoller.Sources.Graph;

/// <summary>Reglages d'une source Microsoft Graph.</summary>
public sealed record GraphSourceOptions
{
    /// <summary>Adresse de la boite surveillee.</summary>
    public required string Mailbox { get; init; }

    /// <summary>Identifiants de l'application.</summary>
    public required GraphCredentials Credentials { get; init; }
}

/// <summary>
/// Collecte les messages par Microsoft Graph.
/// </summary>
/// <remarks>
/// Deux choix structurants :
///
/// 1. DOSSIERS BIEN CONNUS PAR NOM CANONIQUE. Graph accepte directement
///    mailFolders('inbox') et mailFolders('archive'), independamment de la
///    langue de la boite. C'est la reponse definitive au "inbox=Inbox" qui
///    cassait sur les boites francaises : plus aucune resolution par libelle,
///    donc plus de cache de dossiers a maintenir.
///
/// 2. MIME BRUT PAR $value. Le meme flux que MailKit, donc le meme parseur en
///    aval. Le cout est de telecharger le message entier meme pour un filtrage
///    precoce sur le sujet ; a surveiller sur les boites a tres gros volume.
/// </remarks>
public sealed class GraphMailSource(
    GraphSourceOptions options,
    GraphClientProvider clientProvider,
    ILogger<GraphMailSource> logger) : IMailSource
{
    private readonly GraphServiceClient _client = clientProvider.GetClient(options.Credentials);

    /// <inheritdoc />
    public string MailboxId => options.Mailbox;

    /// <inheritdoc />
    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var folder = await _client.Users[options.Mailbox]
                .MailFolders["inbox"]
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var details = new Dictionary<string, string>
            {
                ["Dossier"] = folder?.DisplayName ?? "inconnu",
                ["Messages"] = folder?.TotalItemCount?.ToString(CultureInfo.InvariantCulture) ?? "?",
                ["NonLus"] = folder?.UnreadItemCount?.ToString(CultureInfo.InvariantCulture) ?? "?",
            };

            // Le libelle du dossier est affiche a titre indicatif : il varie
            // selon la langue de la boite et ne doit jamais servir de cle.
            return new ConnectionCheck(
                true, $"Boite {options.Mailbox} accessible", stopwatch.Elapsed, details);
        }
        catch (ODataError ex)
        {
            return ConnectionCheck.Ko(
                $"Graph {ex.Error?.Code} : {ex.Error?.Message}", stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public Task<FolderRef> ResolveFolderAsync(FolderRef folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Aucun appel reseau : le nom canonique est utilisable tel quel dans
        // l'URL. C'est ce qui supprime le cache de dossiers de la v1 et, avec
        // lui, toute une classe de bugs de resolution.
        var canonical = folder.WellKnown switch
        {
            WellKnownFolder.Inbox => "inbox",
            WellKnownFolder.Archive => "archive",
            WellKnownFolder.SentItems => "sentitems",
            WellKnownFolder.DeletedItems => "deleteditems",
            _ => folder.Name,
        };

        return Task.FromResult(new FolderRef(canonical, folder.WellKnown));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MessageRef> ListAsync(
        FolderRef folder,
        MessageQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(query);

        var filters = new List<string>();

        if (query.UnreadOnly)
        {
            filters.Add("isRead eq false");
        }

        if (query.RequireAttachments)
        {
            filters.Add("hasAttachments eq true");
        }

        if (query.Since is { } since)
        {
            filters.Add(
                $"receivedDateTime ge {since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}");
        }

        var page = await _client.Users[options.Mailbox]
            .MailFolders[folder.Name]
            .Messages
            .GetAsync(request =>
            {
                // Selection explicite des champs : sans $select, Graph renvoie
                // le corps complet de chaque message dans le listage, ce qui
                // multiplie le volume transfere par cent pour rien.
                request.QueryParameters.Select =
                [
                    "id", "internetMessageId", "subject", "from", "toRecipients",
                    "ccRecipients", "receivedDateTime", "sentDateTime", "hasAttachments",
                ];

                request.QueryParameters.Orderby = ["receivedDateTime asc"];
                request.QueryParameters.Top = Math.Min(query.MaxCount ?? 50, 100);

                if (filters.Count > 0)
                {
                    request.QueryParameters.Filter = string.Join(" and ", filters);
                }
            },
            cancellationToken).ConfigureAwait(false);

        var returned = 0;
        var max = query.MaxCount ?? int.MaxValue;

        while (page?.Value is not null)
        {
            foreach (var message in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (returned >= max)
                {
                    yield break;
                }

                returned++;
                yield return ToMessageRef(message);
            }

            if (page.OdataNextLink is null || returned >= max)
            {
                yield break;
            }

            // Pagination suivie explicitement : le PageIterator du SDK
            // materialise tout en memoire, ce qui est incompatible avec une
            // boite contenant des dizaines de milliers de messages.
            page = await _client.Users[options.Mailbox]
                .MailFolders[folder.Name]
                .Messages
                .WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenRawMessageAsync(MessageRef message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var stream = await _client.Users[options.Mailbox]
            .Messages[message.Identity.ProviderId]
            .Content
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return stream
            ?? throw new AcPollerException(
                FailureKind.Transient,
                $"Graph n'a renvoye aucun contenu pour {message.Identity.ProviderId}.");
    }

    /// <inheritdoc />
    public async Task ApplyDispositionAsync(
        MessageRef message,
        MessageDisposition disposition,
        FolderRef? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            switch (disposition)
            {
                case MessageDisposition.None:
                    return;

                case MessageDisposition.MarkAsRead:
                    await _client.Users[options.Mailbox]
                        .Messages[message.Identity.ProviderId]
                        .PatchAsync(new Message { IsRead = true }, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MessageDisposition.Move when target is not null:
                    await _client.Users[options.Mailbox]
                        .Messages[message.Identity.ProviderId]
                        .Move
                        .PostAsync(
                            new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
                            {
                                DestinationId = target.WellKnown switch
                                {
                                    WellKnownFolder.Archive => "archive",
                                    WellKnownFolder.DeletedItems => "deleteditems",
                                    _ => target.Name,
                                },
                            },
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case MessageDisposition.Delete:
                    await _client.Users[options.Mailbox]
                        .Messages[message.Identity.ProviderId]
                        .DeleteAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    logger.LogWarning("Disposition {Disposition} sans dossier cible, ignoree", disposition);
                    break;
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Message deja deplace ou supprime, par un autre traitement ou par
            // l'utilisateur. La disposition est idempotente : ce n'est pas une
            // erreur, le travail est deja fait.
            logger.LogDebug(
                "Disposition sans effet, message introuvable : {Id}", message.Identity.ProviderId);
        }
    }

    private static MessageRef ToMessageRef(Message message) => new()
    {
        Identity = new MessageIdentity
        {
            // L'id Graph CHANGE quand le message est deplace de dossier : il
            // sert a l'appel immediat, jamais a la deduplication.
            ProviderId = message.Id ?? string.Empty,
            InternetMessageId = message.InternetMessageId,
            DeduplicationKey = message.InternetMessageId ?? message.Id ?? string.Empty,
        },
        Envelope = new MessageEnvelope
        {
            Subject = message.Subject,
            From = message.From?.EmailAddress?.Address,
            To = [.. message.ToRecipients?.Select(r => r.EmailAddress?.Address ?? string.Empty) ?? []],
            Cc = [.. message.CcRecipients?.Select(r => r.EmailAddress?.Address ?? string.Empty) ?? []],
            ReceivedUtc = message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            SentUtc = message.SentDateTime,
        },
        HasAttachments = message.HasAttachments ?? false,
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Le client est mutualise et appartient au fournisseur : le liberer ici
        // le rendrait inutilisable pour les cent autres boites du meme locataire.
        return ValueTask.CompletedTask;
    }
}

/// <summary>Fabrique de sources Graph.</summary>
public sealed class GraphMailSourceFactory : IMailSourceFactory
{
    /// <inheritdoc />
    public string ProtocolType => "graph";

    /// <inheritdoc />
    public IMailSource Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new GraphSourceOptions
        {
            Mailbox = Required(configuration, "Mailbox"),
            Credentials = new GraphCredentials(
                Required(configuration, "TenantId"),
                Required(configuration, "ClientId"),
                configuration["ClientSecret"],
                configuration["CertificateThumbprint"]),
        };

        return new GraphMailSource(
            options,
            services.GetRequiredService<GraphClientProvider>(),
            services.GetRequiredService<ILogger<GraphMailSource>>());
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new AcPollerException(
            FailureKind.Permanent, $"Source 'graph' : le reglage '{key}' est obligatoire.");
}
