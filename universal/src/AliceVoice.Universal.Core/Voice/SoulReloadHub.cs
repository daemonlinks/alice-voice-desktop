using Serilog;

namespace AliceVoice.Universal.Voice;

/// <summary>
/// After a SOUL save, every Alice LOCAL backend in this process reloads the
/// file: the window's backend and, when the bridge is up, the phone's backend.
/// </summary>
public sealed class SoulReloadHub
{
    private readonly List<Func<CancellationToken, Task>> _reloaders = new();

    public void Register(Func<CancellationToken, Task> reload) => _reloaders.Add(reload);

    public async Task ReloadAllAsync(CancellationToken ct = default)
    {
        foreach (var reload in _reloaders.ToArray())
        {
            try { await reload(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warning(ex, "SOUL reload failed"); }
        }
    }
}
