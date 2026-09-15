using System.Collections.Concurrent;
using System.Net;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Authentication.Azure;

namespace ACPoller.Sources.Graph;

/// <summary>Identifiants d'une application Entra ID.</summary>
/// <param name="TenantId">Locataire.</param>
/// <param name="ClientId">Application.</param>
/// <param name="ClientSecret">Secret client, exclusif avec l'empreinte de certificat.</param>
/// <param name="CertificateThumbprint">Empreinte du certificat, a preferer au secret.</param>
public sealed record GraphCredentials(
    string TenantId,
    string ClientId,
    string? ClientSecret,
    string? CertificateThumbprint);

/// <summary>Reglages de limitation de debit vers Graph.</summary>
public sealed record GraphThrottlingOptions
{
    /// <summary>Requetes simultanees maximales, tous appels confondus.</summary>
    public int MaxConcurrent { get; init; } = 8;

    /// <summary>Espacement minimal entre deux requetes, en millisecondes.</summary>
    public int MinSpacingMs { get; init; } = 50;

    /// <summary>Respecter l'en-tete Retry-After renvoye sur 429.</summary>
    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>Nombre maximal de reprises apres un 429.</summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>
/// Fournit des clients Graph mutualises par jeu d'identifiants.
/// </summary>
/// <remarks>
/// Point central de la refonte : en v1, chaque boite construisait son propre
/// GraphServiceClient, soit 103 clients pour un seul locataire, donc 103 pools
/// de connexions HTTP et 103 caches de jetons. Ici, les boites partageant le
/// meme couple locataire et application partagent un client, et donc un cache
/// de jetons et une limitation de debit commune.
///
/// La limitation doit imperativement etre partagee : borner chaque boite a huit
/// requetes simultanees n'empeche pas cent boites d'en lancer huit cents.
/// </remarks>
public sealed class GraphClientProvider(
    GraphThrottlingOptions throttling,
    ILoggerFactory loggerFactory,
    ILogger<GraphClientProvider> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();

    private readonly ConcurrentDictionary<string, Lazy<GraphServiceClient>> _clients = new();
    private readonly ConcurrentDictionary<string, GraphThrottlingHandler> _handlers = new();

    /// <summary>Obtient le client correspondant aux identifiants, en le creant au besoin.</summary>
    /// <param name="credentials">Identifiants de l'application.</param>
    /// <returns>Le client mutualise.</returns>
    public GraphServiceClient GetClient(GraphCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // Cle sur locataire et application, pas sur la boite : c'est ce qui
        // permet le partage entre les cent boites d'un meme client.
        var key = $"{credentials.TenantId}|{credentials.ClientId}";

        return _clients.GetOrAdd(key, k => new Lazy<GraphServiceClient>(
            () => Create(k, credentials),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private GraphServiceClient Create(string key, GraphCredentials credentials)
    {
        var credential = BuildCredential(credentials);

        var authProvider = new AzureIdentityAuthenticationProvider(
            credential,
            scopes: ["https://graph.microsoft.com/.default"]);

        var handler = new GraphThrottlingHandler(
            throttling,
            loggerFactory.CreateLogger<GraphThrottlingHandler>());

        _handlers[key] = handler;

        var handlers = GraphClientFactory.CreateDefaultHandlers();
        handlers.Add(handler);

        var httpClient = GraphClientFactory.Create(handlers);
        _httpClients[key] = httpClient;

        logger.LogInformation(
            "Client Graph cree pour le locataire {Tenant} (concurrence {Max}, espacement {Spacing} ms)",
            credentials.TenantId, throttling.MaxConcurrent, throttling.MinSpacingMs);

        return new GraphServiceClient(httpClient, authProvider);
    }

    private static Azure.Core.TokenCredential BuildCredential(GraphCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.CertificateThumbprint))
        {
            // Certificat : pas de date d'expiration a surveiller au jour le
            // jour, contrairement au secret client qui expire toujours un
            // vendredi soir.
            return new ClientCertificateCredential(
                credentials.TenantId,
                credentials.ClientId,
                FindCertificate(credentials.CertificateThumbprint));
        }

        if (string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            throw new InvalidOperationException(
                "Graph : ni secret client ni empreinte de certificat fournis.");
        }

        return new ClientSecretCredential(
            credentials.TenantId,
            credentials.ClientId,
            credentials.ClientSecret);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 FindCertificate(string thumbprint)
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);

        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);

        var found = store.Certificates.Find(
            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);

        return found.Count > 0
            ? found[0]
            : throw new InvalidOperationException(
                $"Certificat introuvable dans le magasin machine : {thumbprint}. "
                + "Verifier que le compte de service a acces a sa cle privee.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var httpClient in _httpClients.Values)
        {
            httpClient.Dispose();
        }

        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }

        _httpClients.Clear();
        _handlers.Clear();
        _clients.Clear();
    }
}

/// <summary>
/// Borne le debit des appels Graph : concurrence, espacement, respect du 429.
/// </summary>
/// <remarks>
/// Graph renvoie un 429 avec un en-tete Retry-After quand le quota est depasse.
/// L'ignorer aggrave la situation : le service repart aussitot, reprend un 429,
/// et le quota se reconstitue de plus en plus lentement. C'est le mecanisme qui
/// figeait la v1 sous charge.
/// </remarks>
public sealed class GraphThrottlingHandler(
    GraphThrottlingOptions options,
    ILogger<GraphThrottlingHandler> logger) : DelegatingHandler, IDisposable
{
    private readonly SemaphoreSlim _concurrency = new(options.MaxConcurrent, options.MaxConcurrent);
    private readonly SemaphoreSlim _spacingGate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    /// <summary>
    /// Nombre de reponses 429 recues depuis le demarrage. Statique car le
    /// handler est construit par la fabrique HTTP, hors du conteneur.
    /// </summary>
    public static long ThrottleCount => Interlocked.Read(ref _throttleCount);

    private static long _throttleCount;


    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // Compte AVANT le test de reprise : un 429 au dernier essai
                    // ou avec le respect du Retry-After desactive sort par le
                    // return ci-dessous, et c'est justement le cas le plus
                    // grave. Ne compter que les 429 rejoues masquerait la
                    // saturation reelle.
                    Interlocked.Increment(ref _throttleCount);
                }

                if (response.StatusCode != HttpStatusCode.TooManyRequests
                    || !options.HonorRetryAfter
                    || attempt >= options.MaxRetries)
                {
                    return response;
                }

                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

                logger.LogWarning(
                    "Graph 429, attente de {Delay} avant nouvelle tentative ({Attempt}/{Max})",
                    delay, attempt + 1, options.MaxRetries);

                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Impose un espacement minimal entre deux departs de requete. Sans lui,
    /// huit appels simultanes partent a la meme milliseconde et declenchent le
    /// throttling avant meme d'avoir atteint la limite de concurrence.
    /// </summary>
    private async Task ApplySpacingAsync(CancellationToken cancellationToken)
    {
        if (options.MinSpacingMs <= 0)
        {
            return;
        }

        await _spacingGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = _nextAllowed - now;

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
            }

            _nextAllowed = now.AddMilliseconds(options.MinSpacingMs);
        }
        finally
        {
            _spacingGate.Release();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _concurrency.Dispose();
            _spacingGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
