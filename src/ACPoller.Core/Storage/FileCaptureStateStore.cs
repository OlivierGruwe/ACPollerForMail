using System.Text.Json;
using System.Text.Json.Serialization;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Infrastructure;
using ACPoller.Abstractions.Model;
using Microsoft.Extensions.Logging;

namespace ACPoller.Core.Storage;

/// <summary>
/// Persiste l'etat d'un message en cours dans son propre repertoire de travail.
/// </summary>
/// <remarks>
/// Le manifest vit AVEC les fichiers qu'il decrit, pas dans une base separee.
/// Consequence directe : supprimer un repertoire de travail supprime aussi son
/// etat, et il ne peut jamais exister d'entree orpheline pointant vers des
/// fichiers disparus. C'est le mode de defaillance le plus penible a diagnostiquer
/// quand les deux sont dissocies.
/// </remarks>
public sealed class FileCaptureStateStore(string rootDirectory, ILogger<FileCaptureStateStore> logger)
    : ICaptureStateStore
{
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task SaveAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = Path.Combine(context.WorkDirectory, ManifestName);
        var temporary = path + ".tmp";

        Directory.CreateDirectory(context.WorkDirectory);

        // Ecriture atomique : un manifest tronque par une coupure de courant
        // rendrait la reprise impossible et le repertoire indechiffrable.
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                stream, ManifestDto.Create(context), SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <inheritdoc />
    public async Task<CaptureContext?> LoadAsync(string correlationId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootDirectory, correlationId, ManifestName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var dto = await JsonSerializer
                .DeserializeAsync<ManifestDto>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return dto?.ToContext();
        }
        catch (JsonException ex)
        {
            // Manifest illisible : on repart de zero pour ce message plutot que
            // de bloquer. Le repertoire est conserve pour le diagnostic.
            logger.LogWarning(ex, "Manifest illisible, reprise abandonnee pour {CorrelationId}", correlationId);
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CaptureContext> LoadPendingAsync(
        string mailboxId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(Path.Combine(directory, ManifestName)))
            {
                continue;
            }

            var context = await LoadAsync(Path.GetFileName(directory), cancellationToken).ConfigureAwait(false);

            if (context is not null && context.MailboxId.Equals(mailboxId, StringComparison.OrdinalIgnoreCase))
            {
                yield return context;
            }
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string correlationId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootDirectory, correlationId, ManifestName);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    // Les DTO existent parce que le modele de domaine porte des collections en
    // lecture seule et des proprietes "required" : le serialiseur ne sait pas
    // les reconstruire. Les mapper explicitement coute quelques lignes et evite
    // de degrader le modele pour les besoins de la serialisation.
    private sealed record ManifestDto
    {
        /// <summary>Version du format de manifest, pour une migration future.</summary>
        public int Version { get; init; } = 1;

        public required string CorrelationId { get; init; }
        public required string MailboxId { get; init; }
        public required string ConfigurationName { get; init; }
        public required string WorkDirectory { get; init; }
        public string? RawMessagePath { get; init; }
        public int Attempt { get; init; }
        public DateTimeOffset StartedUtc { get; init; }

        public required string ProviderId { get; init; }
        public string? InternetMessageId { get; init; }
        public required string DeduplicationKey { get; init; }

        public string? Subject { get; init; }
        public string? From { get; init; }
        public List<string> To { get; init; } = [];
        public List<string> Cc { get; init; } = [];
        public DateTimeOffset ReceivedUtc { get; init; }
        public DateTimeOffset? SentUtc { get; init; }
        public Dictionary<string, string> Headers { get; init; } = [];

        public Dictionary<string, string> Properties { get; init; } = [];
        public List<FailureDto> Warnings { get; init; } = [];
        public required NodeDto Root { get; init; }

        public static ManifestDto Create(CaptureContext context) => new()
        {
            CorrelationId = context.CorrelationId,
            MailboxId = context.MailboxId,
            ConfigurationName = context.ConfigurationName,
            WorkDirectory = context.WorkDirectory,
            RawMessagePath = context.RawMessagePath,
            Attempt = context.Attempt,
            StartedUtc = context.StartedUtc,
            ProviderId = context.Identity.ProviderId,
            InternetMessageId = context.Identity.InternetMessageId,
            DeduplicationKey = context.Identity.DeduplicationKey,
            Subject = context.Envelope.Subject,
            From = context.Envelope.From,
            To = [.. context.Envelope.To],
            Cc = [.. context.Envelope.Cc],
            ReceivedUtc = context.Envelope.ReceivedUtc,
            SentUtc = context.Envelope.SentUtc,
            Headers = new Dictionary<string, string>(context.Envelope.Headers),
            Properties = new Dictionary<string, string>(context.Properties),
            Warnings = [.. context.Warnings.Select(FailureDto.Create)],
            Root = NodeDto.Create(context.Root),
        };

        public CaptureContext ToContext()
        {
            var context = new CaptureContext
            {
                CorrelationId = CorrelationId,
                MailboxId = MailboxId,
                ConfigurationName = ConfigurationName,
                WorkDirectory = WorkDirectory,
                RawMessagePath = RawMessagePath,
                Attempt = Attempt,
                StartedUtc = StartedUtc,
                Identity = new MessageIdentity
                {
                    ProviderId = ProviderId,
                    InternetMessageId = InternetMessageId,
                    DeduplicationKey = DeduplicationKey,
                },
                Envelope = new MessageEnvelope
                {
                    Subject = Subject,
                    From = From,
                    To = To,
                    Cc = Cc,
                    ReceivedUtc = ReceivedUtc,
                    SentUtc = SentUtc,
                    Headers = Headers,
                },
                Root = Root.ToNode(),
            };

            foreach (var (key, value) in Properties)
            {
                context.Properties[key] = value;
            }

            foreach (var warning in Warnings)
            {
                context.Warnings.Add(warning.ToFailure());
            }

            return context;
        }
    }

    private sealed record NodeDto
    {
        public required string LogicalPath { get; init; }
        public required DocumentKind Kind { get; init; }
        public required string FileName { get; init; }
        public int Order { get; init; }
        public string? ContentType { get; init; }
        public long SizeBytes { get; init; }
        public string? Sha256 { get; init; }
        public string? SourcePath { get; init; }
        public string? PdfPath { get; init; }
        public int? PageCount { get; init; }
        public NodeStatus Status { get; init; }
        public FailureDto? Failure { get; init; }
        public string? ConvertedBy { get; init; }
        public int Depth { get; init; }
        public Dictionary<string, string> Properties { get; init; } = [];
        public List<NodeDto> Children { get; init; } = [];

        public static NodeDto Create(DocumentNode node) => new()
        {
            LogicalPath = node.LogicalPath,
            Kind = node.Kind,
            FileName = node.FileName,
            Order = node.Order,
            ContentType = node.ContentType,
            SizeBytes = node.SizeBytes,
            Sha256 = node.Sha256,
            SourcePath = node.SourcePath,
            PdfPath = node.PdfPath,
            PageCount = node.PageCount,
            Status = node.Status,
            Failure = node.Failure is null ? null : FailureDto.Create(node.Failure),
            ConvertedBy = node.ConvertedBy,
            Depth = node.Depth,
            Properties = new Dictionary<string, string>(node.Properties),
            Children = [.. node.Children.Select(Create)],
        };

        public DocumentNode ToNode()
        {
            var node = new DocumentNode(LogicalPath, Kind, FileName)
            {
                Order = Order,
                ContentType = ContentType,
                SizeBytes = SizeBytes,
                Sha256 = Sha256,
                SourcePath = SourcePath,
                PdfPath = PdfPath,
                PageCount = PageCount,
                Status = Status,
                Failure = Failure?.ToFailure(),
                ConvertedBy = ConvertedBy,
                Depth = Depth,
            };

            foreach (var (key, value) in Properties)
            {
                node.Properties[key] = value;
            }

            foreach (var child in Children)
            {
                node.AddChild(child.ToNode());
            }

            return node;
        }
    }

    private sealed record FailureDto(FailureKind Kind, string Reason, string? Code)
    {
        // L'exception d'origine n'est pas serialisee : elle n'a pas de sens
        // apres redemarrage, et sa stack trace n'est plus exploitable.
        public static FailureDto Create(Failure failure) => new(failure.Kind, failure.Reason, failure.Code);

        public Failure ToFailure() => new(Kind, Reason, Code);
    }
}
