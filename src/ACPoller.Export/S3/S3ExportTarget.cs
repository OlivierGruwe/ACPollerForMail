using System.Diagnostics;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using ACPoller.Abstractions.Common;
using ACPoller.Abstractions.Export;
using ACPoller.Abstractions.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ACPoller.Export.S3;

/// <summary>Reglages d'une cible S3.</summary>
public sealed record S3TargetOptions
{
    /// <summary>Nom d'instance, repris dans les journaux.</summary>
    public string Name { get; init; } = "s3";

    /// <summary>Bucket de destination.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Prefixe des cles, deja resolu par le moteur de jetons.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Region AWS, ex. eu-west-3.</summary>
    public string Region { get; init; } = "eu-west-3";

    /// <summary>
    /// Cle d'acces. Laisser vide pour utiliser la chaine de resolution AWS
    /// standard : profil de la machine, role IAM, variables d'environnement.
    /// C'est preferable, un secret non stocke ne peut pas fuiter.
    /// </summary>
    public string? AccessKey { get; init; }

    /// <summary>Cle secrete, dechiffree a la construction.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Point d'acces personnalise, pour un stockage compatible S3 (MinIO, Scality).</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Chiffrement au repos : "AES256", "aws:kms" ou vide.</summary>
    public string? ServerSideEncryption { get; init; } = "AES256";
}

/// <summary>
/// Depose les fichiers dans un bucket S3 ou compatible.
/// </summary>
/// <remarks>
/// L'ecriture d'un objet S3 est atomique par construction : un objet est visible
/// une fois complet, jamais partiellement. Le double temps du systeme de
/// fichiers et du FTP est donc inutile ici, et le reglage AtomicWrite est
/// simplement sans effet.
///
/// TransferUtility gere seul le decoupage en parties pour les objets
/// volumineux, ce qui compte des qu'un PDF unifie depasse quelques dizaines de
/// mega-octets.
/// </remarks>
public sealed class S3ExportTarget : IExportTarget
{
    private readonly S3TargetOptions _options;
    private readonly ILogger<S3ExportTarget> _logger;
    private readonly Amazon.S3.AmazonS3Client _client;

    /// <summary>Construit la cible et son client S3.</summary>
    /// <param name="options">Reglages.</param>
    /// <param name="logger">Journalisation.</param>
    public S3ExportTarget(S3TargetOptions options, ILogger<S3ExportTarget> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;
        _client = CreateClient(options);
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public async Task<ConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Listage borne a un objet : verifie a la fois l'existence du
            // bucket, les droits de lecture et la resolution des identifiants,
            // sans ramener des milliers de cles.
            var response = await _client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.Bucket,
                    Prefix = _options.Prefix,
                    MaxKeys = 1,
                },
                cancellationToken).ConfigureAwait(false);

            return ConnectionCheck.Ok(
                $"Bucket {_options.Bucket} accessible ({response.HttpStatusCode})",
                stopwatch.Elapsed);
        }
        catch (AmazonS3Exception ex)
        {
            return ConnectionCheck.Ko($"S3 {ex.ErrorCode} : {ex.Message}", stopwatch.Elapsed);
        }
        catch (AmazonServiceException ex)
        {
            return ConnectionCheck.Ko($"Service AWS : {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var written = new List<string>();

        try
        {
            using var transfer = new TransferUtility(_client);

            foreach (var item in batch.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = BuildKey(item.RelativeName);

                var request = new TransferUtilityUploadRequest
                {
                    BucketName = _options.Bucket,
                    Key = key,
                    FilePath = item.LocalPath,
                };

                if (!string.IsNullOrWhiteSpace(_options.ServerSideEncryption))
                {
                    request.ServerSideEncryptionMethod =
                        ServerSideEncryptionMethod.FindValue(_options.ServerSideEncryption);
                }

                await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
                written.Add($"s3://{_options.Bucket}/{key}");
            }

            if (!string.IsNullOrWhiteSpace(batch.Policy.SentinelFileName))
            {
                var sentinelKey = BuildKey(batch.Policy.SentinelFileName
                    .Replace("{name}", batch.CorrelationId, StringComparison.Ordinal));

                await _client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = _options.Bucket,
                        Key = sentinelKey,
                        ContentBody = batch.CorrelationId,
                    },
                    cancellationToken).ConfigureAwait(false);

                written.Add($"s3://{_options.Bucket}/{sentinelKey}");
            }

            _logger.LogDebug(
                "{Target} : {Count} objet(s) deposes pour {CorrelationId}",
                Name, written.Count, batch.CorrelationId);

            return ExportResult.Ok(written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonS3Exception ex)
        {
            // Les erreurs 4xx viennent d'une configuration fautive : bucket
            // inexistant, droits manquants, region erronee. Rejouer n'y change
            // rien. Les 5xx et le throttling sont transitoires.
            var failure = (int)ex.StatusCode is >= 400 and < 500 && ex.ErrorCode != "SlowDown"
                ? Failure.Permanent($"S3 {ex.ErrorCode} : {ex.Message}", ex.ErrorCode, ex)
                : Failure.Transient($"S3 {ex.ErrorCode} : {ex.Message}", ex.ErrorCode, ex);

            return ExportResult.Ko(failure);
        }
        catch (AmazonServiceException ex)
        {
            return ExportResult.Ko(Failure.Transient($"Service AWS : {ex.Message}", exception: ex));
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(
        ExportBatch batch,
        IReadOnlyList<string> writtenReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(writtenReferences);

        foreach (var reference in writtenReferences.Reverse())
        {
            try
            {
                var key = reference.Replace($"s3://{_options.Bucket}/", string.Empty, StringComparison.Ordinal);

                await _client.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = _options.Bucket, Key = key },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonServiceException ex)
            {
                _logger.LogError(ex, "Rollback incomplet, objet non supprime : {Reference}", reference);
            }
        }
    }

    private string BuildKey(string relativeName)
    {
        // Cles S3 toujours en slash, et pas de segment vide qui produirait un
        // double slash interprete comme un repertoire fantome.
        var normalized = relativeName.Replace('\\', '/').TrimStart('/');
        var prefix = _options.Prefix.TrimEnd('/');

        return prefix.Length == 0 ? normalized : $"{prefix}/{normalized}";
    }

    private static Amazon.S3.AmazonS3Client CreateClient(S3TargetOptions options)
    {
        var config = new AmazonS3Config();

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            // Stockage compatible S3 : MinIO, Garage, Ceph, Scality, OVH.
            // Style chemin obligatoire, ces implementations ne gerent pas le
            // style hote virtuel.
            config.ServiceURL = options.ServiceUrl;
            config.ForcePathStyle = true;

            // Depuis AWSSDK v4, le SDK calcule un checksum CRC64-NVME sur tous
            // les Put et le valide sur les Get. AWS le comprend, la plupart des
            // implementations compatibles non : l'erreur remonte en
            // MissingContentMD5 ou BadDigest, sans designer la cause.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;

            // Region formelle : beaucoup d'implementations l'ignorent, mais la
            // signature SigV4 en a besoin. Garage utilise souvent "garage".
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            // Chaine de resolution standard : profil machine, role IAM,
            // variables d'environnement. A preferer systematiquement.
            return new AmazonS3Client(config);
        }

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            config);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Fabrique de cibles S3.</summary>
public sealed class S3ExportTargetFactory : IExportTargetFactory
{
    /// <inheritdoc />
    public string TargetType => "s3";

    /// <inheritdoc />
    public IExportTarget Create(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var protector = services.GetRequiredService<ISecretProtector>();
        var secretKey = configuration["SecretKey"];

        var options = new S3TargetOptions
        {
            Name = configuration["Name"] ?? "s3",
            Bucket = configuration["Bucket"] ?? throw new AcPollerException(
                FailureKind.Permanent, "Cible 's3' : le reglage 'Bucket' est obligatoire."),
            Prefix = configuration["Prefix"] ?? string.Empty,
            Region = configuration["Region"] ?? "eu-west-3",
            AccessKey = configuration["AccessKey"],
            SecretKey = string.IsNullOrEmpty(secretKey) ? null : protector.Unprotect(secretKey),
            ServiceUrl = configuration["ServiceUrl"],
            ServerSideEncryption = configuration["ServerSideEncryption"] ?? "AES256",
        };

        return new S3ExportTarget(options, services.GetRequiredService<ILogger<S3ExportTarget>>());
    }
}
