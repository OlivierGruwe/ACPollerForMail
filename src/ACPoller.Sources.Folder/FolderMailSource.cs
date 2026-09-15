using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Model;
using ACPoller.Abstractions.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ACPoller.Sources.Folder;

/// <summary>Reglages d'une source repertoire.</summary>
public sealed record FolderSourceOptions
{
    /// <summary>Repertoire contenant les fichiers .eml a traiter.</summary>
    public required string Path { get; init; }

    /// <summary>Identifiant de boite fictif, utilise comme cle du journal des traitements.</summary>
    public string Mailbox { get; init; } = "folder@local";

    /// <summary>Repertoire ou deplacer les fichiers traites. Null pour les laisser en place.</summary>
    public string? ProcessedPath { get; init; }
}

/// <summary>
/// Lit des messages depuis un repertoire de fichiers .eml.
/// </summary>
/// <remarks>
/// Trois usages, tous reels :
///
/// 1. DEVELOPPEMENT. Faire tourner la chaine complete sur le corpus de messages
///    atypiques, sans boite ni reseau, et voir les PDF et fichiers
///    d'information reellement produits.
/// 2. RECETTE CLIENT. Rejouer un lot de messages representatifs autant de fois
///    que necessaire, avec des reglages differents, sans dependre d'une boite
///    partagee que quelqu'un vide entre deux essais.
/// 3. INCIDENT DE PRODUCTION. Le MIME brut est conserve dans le repertoire de
///    travail : deposer ce fichier ici rejoue exactement le message fautif, sur
///    un poste de developpement, avec un point d'arret.
///
/// Ce n'est donc pas un bouchon de test, c'est un outil d'exploitation.
/// </remarks>
public sealed class FolderMailSource(
    FolderSourceOptions options,
    ILogger<FolderMailSource> logger) : IMailSource
{
    /// <inheritdoc />
    public string MailboxId => options.Mailbox;

    /// <inheritdoc />
    public Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!Directory.Exists(options.Path))
        {
            return Task.FromResult(
                ConnectionCheck.Ko($"Repertoire inexistant : {options.Path}", stopwatch.Elapsed));
        }

        var count = Directory.EnumerateFiles(options.Path, "*.eml").Count();

        return Task.FromResult(new ConnectionCheck(
            true,
            $"{count} message(s) dans {options.Path}",
            stopwatch.Elapsed,
            new Dictionary<string, string> { ["Fichiers"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    }

    /// <inheritdoc />
    public Task<FolderRef> ResolveFolderAsync(FolderRef folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return Task.FromResult(folder);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MessageRef> ListAsync(
        FolderRef folder,
        MessageQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!Directory.Exists(options.Path))
        {
            logger.LogWarning("Repertoire source inexistant : {Path}", options.Path);
            yield break;
        }

        var files = Directory.EnumerateFiles(options.Path, "*.eml").OrderBy(f => f).ToArray();
        var returned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (returned >= (query.MaxCount ?? int.MaxValue))
            {
                yield break;
            }

            MessageRef? reference;

            try
            {
                reference = await BuildReferenceAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                // Un .eml illisible est ecarte sans arreter le lot : c'est le
                // comportement d'une vraie source face a un message atypique.
                logger.LogWarning(ex, "Fichier illisible ignore : {File}", file);
                continue;
            }

            returned++;
            yield return reference;
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenRawMessageAsync(MessageRef message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // L'identifiant est le chemin du fichier : la source est locale, il n'y
        // a rien a resoudre.
        return Task.FromResult<Stream>(File.OpenRead(message.Identity.ProviderId));
    }

    /// <inheritdoc />
    public Task ApplyDispositionAsync(
        MessageRef message,
        MessageDisposition disposition,
        FolderRef? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var path = message.Identity.ProviderId;

        switch (disposition)
        {
            case MessageDisposition.Delete:
                File.Delete(path);
                break;

            case MessageDisposition.Move when !string.IsNullOrWhiteSpace(options.ProcessedPath):
                Directory.CreateDirectory(options.ProcessedPath);
                File.Move(path, Path.Combine(options.ProcessedPath, Path.GetFileName(path)), overwrite: true);
                break;

            default:
                // None et MarkAsRead laissent le fichier en place : c'est ce qui
                // permet de rejouer indefiniment le meme corpus en changeant les
                // reglages. Le journal des traitements evite la boucle.
                break;
        }

        return Task.CompletedTask;
    }

    private static async Task<MessageRef> BuildReferenceAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        var messageId = message.MessageId;

        // Sans Message-Id, empreinte du chemin : deux corpus differents ne se
        // telescopent pas dans le journal des traitements.
        var dedup = string.IsNullOrWhiteSpace(messageId)
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file)))
            : messageId;

        return new MessageRef
        {
            Identity = new MessageIdentity
            {
                ProviderId = file,
                InternetMessageId = messageId,
                DeduplicationKey = dedup,
            },
            Envelope = new MessageEnvelope
            {
                Subject = message.Subject,
                From = message.From.Mailboxes.FirstOrDefault()?.Address,
                To = [.. message.To.Mailboxes.Select(m => m.Address)],
                Cc = [.. message.Cc.Mailboxes.Select(m => m.Address)],
                ReceivedUtc = message.Date.UtcDateTime,
                SentUtc = message.Date,
            },
            HasAttachments = message.Attachments.Any(),
            EstimatedSize = new FileInfo(file).Length,
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fabrique de sources repertoire.</summary>
public sealed class FolderMailSourceFactory : IMailSourceFactory
{
    /// <inheritdoc />
    public string ProtocolType => "folder";

    /// <inheritdoc />
    public IMailSource Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FolderSourceOptions
        {
            Path = configuration["Path"] ?? throw new AcPollerException(
                FailureKind.Permanent, "Source 'folder' : le reglage 'Path' est obligatoire."),
            Mailbox = configuration["Mailbox"] ?? "folder@local",
            ProcessedPath = configuration["ProcessedPath"],
        };

        return new FolderMailSource(options, services.GetRequiredService<ILogger<FolderMailSource>>());
    }
}
