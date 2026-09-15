using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;
using ACPoller.Abstractions.Sources;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ACPoller.Sources.Imap;

/// <summary>Reglages d'une source IMAP.</summary>
public sealed record ImapSourceOptions
{
    /// <summary>Hote.</summary>
    public required string Host { get; init; }

    /// <summary>Port. 993 en SSL implicite, 143 en STARTTLS.</summary>
    public int Port { get; init; } = 993;

    /// <summary>Utiliser SSL implicite. Faux pour STARTTLS.</summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>Utilisateur.</summary>
    public required string UserName { get; init; }

    /// <summary>Mot de passe.</summary>
    public required string Password { get; init; }

    /// <summary>Adresse de la boite, utilisee comme cle du journal des traitements.</summary>
    public required string Mailbox { get; init; }
}

/// <summary>
/// Collecte les messages par IMAP.
/// </summary>
/// <remarks>
/// MailKit expose le MIME brut par <c>GetStreamAsync</c>, exactement comme Graph
/// par son endpoint $value. Les deux sources produisent donc le meme flux, et
/// un seul parseur les traite en aval.
///
/// L'ImapClient n'est pas utilisable depuis plusieurs threads : une instance
/// par source, et la source est creee a chaque cycle par le worker.
/// </remarks>
public sealed class ImapMailSource(
    ImapSourceOptions options,
    ILogger<ImapMailSource> logger) : IMailSource
{
    private readonly ImapClient _client = new();
    private IMailFolder? _currentFolder;

    /// <inheritdoc />
    public string MailboxId => options.Mailbox;

    /// <inheritdoc />
    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var inbox = _client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            var details = new Dictionary<string, string>
            {
                ["Messages"] = inbox.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["NonLus"] = inbox.Unread.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            return new ConnectionCheck(true, $"Connexion etablie sur {options.Host}", stopwatch.Elapsed, details);
        }
        catch (AuthenticationException ex)
        {
            return ConnectionCheck.Ko($"Authentification refusee : {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is ImapProtocolException or IOException or SocketException)
        {
            return ConnectionCheck.Ko($"{ex.GetType().Name} : {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<FolderRef> ResolveFolderAsync(FolderRef folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        // SPECIAL-USE quand le serveur l'annonce : c'est la seule facon fiable
        // de designer un dossier sans dependre de la langue de la boite.
        var resolved = folder.WellKnown switch
        {
            WellKnownFolder.Inbox => _client.Inbox,
            WellKnownFolder.Archive => TryGetSpecial(SpecialFolder.Archive),
            WellKnownFolder.SentItems => TryGetSpecial(SpecialFolder.Sent),
            WellKnownFolder.DeletedItems => TryGetSpecial(SpecialFolder.Trash),
            _ => null,
        };

        if (resolved is null && !string.IsNullOrWhiteSpace(folder.Name))
        {
            resolved = await _client.GetFolderAsync(folder.Name, cancellationToken).ConfigureAwait(false);
        }

        if (resolved is null)
        {
            throw new AcPollerException(
                FailureKind.Permanent,
                $"Dossier introuvable : {folder.WellKnown} / {folder.Name}");
        }

        _currentFolder = resolved;
        return new FolderRef(resolved.FullName, folder.WellKnown);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MessageRef> ListAsync(
        FolderRef folder,
        MessageQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var target = _currentFolder ?? _client.Inbox;

        // ReadWrite : la disposition (marquage lu, deplacement) suit dans le
        // meme cycle, rouvrir en ecriture ensuite couterait un aller-retour.
        if (!target.IsOpen)
        {
            await target.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        }

        var search = BuildQuery(query);
        var uids = await target.SearchAsync(search, cancellationToken).ConfigureAwait(false);

        if (query.MaxCount is { } max && uids.Count > max)
        {
            // Borne le cycle : une boite laissee sans traitement pendant des
            // semaines ne doit pas produire un cycle de plusieurs heures.
            uids = [.. uids.Take(max)];
        }

        // Les en-tetes sont ramenes en UNE requete pour tout le lot : un
        // aller-retour par message multiplierait la duree du cycle par cent.
        var summaries = await target.FetchAsync(
            uids,
            MessageSummaryItems.UniqueId
                | MessageSummaryItems.Envelope
                | MessageSummaryItems.Size
                | MessageSummaryItems.BodyStructure
                | MessageSummaryItems.InternalDate,
            cancellationToken).ConfigureAwait(false);

        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToMessageRef(summary);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenRawMessageAsync(MessageRef message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var target = _currentFolder ?? _client.Inbox;
        var uid = new UniqueId(uint.Parse(message.Identity.ProviderId, System.Globalization.CultureInfo.InvariantCulture));

        // Flux du MIME brut, identique a ce que fournit Graph par $value.
        return await target.GetStreamAsync(uid, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyDispositionAsync(
        MessageRef message,
        MessageDisposition disposition,
        FolderRef? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (disposition == MessageDisposition.None)
        {
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var folder = _currentFolder ?? _client.Inbox;
        var uid = new UniqueId(uint.Parse(message.Identity.ProviderId, System.Globalization.CultureInfo.InvariantCulture));

        switch (disposition)
        {
            case MessageDisposition.MarkAsRead:
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MessageDisposition.Move when target is not null:
            {
                var destination = await ResolveDestinationAsync(target, cancellationToken).ConfigureAwait(false);
                await folder.MoveToAsync(uid, destination, cancellationToken).ConfigureAwait(false);
                break;
            }

            case MessageDisposition.Delete:
                await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, cancellationToken)
                    .ConfigureAwait(false);
                await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                logger.LogWarning(
                    "Disposition {Disposition} sans dossier cible, ignoree", disposition);
                break;
        }
    }

    private async Task<IMailFolder> ResolveDestinationAsync(FolderRef target, CancellationToken cancellationToken)
    {
        var special = target.WellKnown switch
        {
            WellKnownFolder.Archive => TryGetSpecial(SpecialFolder.Archive),
            WellKnownFolder.DeletedItems => TryGetSpecial(SpecialFolder.Trash),
            _ => null,
        };

        return special
            ?? await _client.GetFolderAsync(target.Name, cancellationToken).ConfigureAwait(false);
    }

    private IMailFolder? TryGetSpecial(SpecialFolder special)
    {
        try
        {
            return _client.GetFolder(special);
        }
        catch (NotSupportedException)
        {
            // Serveur sans SPECIAL-USE : repli sur le nom, gere par l'appelant.
            return null;
        }
    }

    private static SearchQuery BuildQuery(MessageQuery query)
    {
        var search = query.UnreadOnly ? SearchQuery.NotSeen : SearchQuery.All;

        if (query.Since is { } since)
        {
            search = search.And(SearchQuery.DeliveredAfter(since.UtcDateTime));
        }

        return search;
    }

    private static MessageRef ToMessageRef(IMessageSummary summary)
    {
        var messageId = summary.Envelope?.MessageId;

        // Cle de deduplication : le Message-Id quand il existe, sinon une
        // empreinte des en-tetes. L'UID IMAP ne convient pas, il change lors
        // d'un deplacement de dossier et se reinitialise a la recreation du
        // dossier cote serveur.
        var dedup = string.IsNullOrWhiteSpace(messageId)
            ? BuildFallbackKey(summary)
            : messageId;

        return new MessageRef
        {
            Identity = new MessageIdentity
            {
                ProviderId = summary.UniqueId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                InternetMessageId = messageId,
                DeduplicationKey = dedup,
            },
            Envelope = new MessageEnvelope
            {
                Subject = summary.Envelope?.Subject,
                From = summary.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address,
                To = [.. summary.Envelope?.To?.Mailboxes.Select(m => m.Address) ?? []],
                Cc = [.. summary.Envelope?.Cc?.Mailboxes.Select(m => m.Address) ?? []],
                ReceivedUtc = summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.UtcNow,
                SentUtc = summary.Envelope?.Date,
            },
            HasAttachments = summary.Attachments?.Any() == true,
            EstimatedSize = summary.Size,
        };
    }

    private static string BuildFallbackKey(IMessageSummary summary)
    {
        var material = string.Join('|',
            summary.Envelope?.From?.ToString() ?? string.Empty,
            summary.Envelope?.Subject ?? string.Empty,
            summary.InternalDate?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            summary.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

        return Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected && _client.IsAuthenticated)
        {
            return;
        }

        if (!_client.IsConnected)
        {
            var security = options.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await _client.ConnectAsync(options.Host, options.Port, security, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_client.IsAuthenticated)
        {
            await _client.AuthenticateAsync(options.UserName, options.Password, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                // Deconnexion propre : un LOGOUT evite que le serveur garde la
                // session ouverte jusqu'a son propre delai d'expiration, ce qui
                // sature le nombre de connexions simultanees autorisees.
                await _client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException)
        {
            logger.LogDebug(ex, "Deconnexion IMAP en erreur, ignoree");
        }

        _client.Dispose();
    }
}

/// <summary>Fabrique de sources IMAP.</summary>
public sealed class ImapMailSourceFactory : IMailSourceFactory
{
    /// <inheritdoc />
    public string ProtocolType => "imap";

    /// <inheritdoc />
    public IMailSource Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ImapSourceOptions
        {
            Host = Required(configuration, "Host"),
            Port = int.TryParse(configuration["Port"], out var port) ? port : 993,
            UseSsl = !bool.TryParse(configuration["UseSsl"], out var ssl) || ssl,
            UserName = Required(configuration, "UserName"),
            Password = configuration["Password"] ?? string.Empty,
            Mailbox = Required(configuration, "Mailbox"),
        };

        return new ImapMailSource(options, services.GetRequiredService<ILogger<ImapMailSource>>());
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new AcPollerException(
            FailureKind.Permanent, $"Source 'imap' : le reglage '{key}' est obligatoire.");
}
