namespace ACPoller.Core.Workers;

/// <summary>
/// Borne le nombre de messages traites simultanement, TOUTES boites confondues.
/// </summary>
/// <remarks>
/// C'est le correctif de fond du modele v1, ou la borne etait par boite : avec
/// une centaine de configurations, cent workers pouvaient saturer ensemble le
/// disque, LibreOffice et l'API Graph, chacun restant pourtant dans sa limite.
/// La ressource partagee est la machine, la borne doit donc etre globale.
/// </remarks>
public sealed class GlobalConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Construit le limiteur.</summary>
    /// <param name="maxConcurrency">Nombre maximal de traitements simultanes.</param>
    public GlobalConcurrencyLimiter(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>Nombre maximal de traitements simultanes.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Places actuellement disponibles, pour la supervision.</summary>
    public int Available => _semaphore.CurrentCount;

    /// <summary>Prend une place, en attendant qu'une se libere.</summary>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Un jeton a liberer pour rendre la place.</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(_semaphore);
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Liberation idempotente : un double Dispose ne doit pas relacher
            // deux places et faire sauter la borne.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>Execute une operation en occupant une place, liberee dans tous les cas.</summary>
    /// <typeparam name="T">Type du resultat.</typeparam>
    /// <param name="operation">Operation a executer.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    /// <returns>Le resultat de l'operation.</returns>
    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // La liberation est ici, pas chez l'appelant : une place oubliee
            // reduit silencieusement le debit du service jusqu'a le figer.
            _semaphore.Release();
        }
    }
}
