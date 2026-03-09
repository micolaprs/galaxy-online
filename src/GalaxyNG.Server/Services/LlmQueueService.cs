using Microsoft.Extensions.Logging;

namespace GalaxyNG.Server.Services;

public sealed class LlmQueueService(ILogger<LlmQueueService> logger)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _holderRace;
    private DateTime _leaseExpiry;
    private readonly object _lock = new();

    public bool TryAcquire(string raceName, int leaseDurationSeconds)
    {
        lock (_lock)
        {
            if (_holderRace is not null && DateTime.UtcNow >= _leaseExpiry)
            {
                logger.LogWarning("LLM slot lease expired for {Race}, releasing", _holderRace);
                _holderRace = null;
                _semaphore.Release();
            }
        }
        if (!_semaphore.Wait(0))
        {
            return false;
        }

        lock (_lock)
        {
            _holderRace = raceName;
            _leaseExpiry = DateTime.UtcNow.AddSeconds(leaseDurationSeconds);
        }
        return true;
    }

    public bool Release(string raceName)
    {
        lock (_lock)
        {
            if (_holderRace is null || !_holderRace.Equals(raceName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _holderRace = null;
            _semaphore.Release();
        }
        return true;
    }
}
