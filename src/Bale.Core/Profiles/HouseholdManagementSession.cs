using Bale.Core.Api.Models;

namespace Bale.Core.Profiles;

/// <summary>HouseholdManagementSession.kt: an ephemeral primary proof, never the device's selected profile.</summary>
public sealed class HouseholdManagementSession(
    Func<string, string, CancellationToken, Task<string?>> verifyPin,
    Func<long> accountGeneration)
{
    private sealed record Held(Profile Primary, string? Token, long Generation);
    public sealed record ReverifyState(Profile Profile, bool IsVerifying = false, string? Error = null);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _renewLock = new(1, 1);
    private Held? _held;
    private TaskCompletionSource<Held?>? _answer;
    private ReverifyState? _reverify;
    public event Action? Changed;
    public bool IsActive { get { lock (_sync) return _held is not null; } }
    public ReverifyState? Reverify { get { lock (_sync) return _reverify; } }

    public void Begin(Profile primary, string? token)
    {
        if (string.IsNullOrEmpty(primary.Id) || (primary.HasPin == true && string.IsNullOrWhiteSpace(token)))
            throw new InvalidOperationException("Enter the primary profile's PIN to manage profiles.");
        End();
        lock (_sync) _held = new(primary, token, accountGeneration());
        Changed?.Invoke();
    }

    public void End()
    {
        lock (_sync)
        {
            _held = null;
            _reverify = null;
            _answer?.TrySetResult(null);
        }
        Changed?.Invoke();
    }

    public async Task SubmitPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        Held current;
        lock (_sync)
        {
            if (_held is not { } held || _reverify is not { IsVerifying: false }) return;
            current = held;
            _reverify = new(held.Primary, true);
        }
        Changed?.Invoke();
        string? token = null;
        string? error = null;
        try
        {
            CheckCurrent(current);
            token = await verifyPin(current.Primary.Id!, pin, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckCurrent(current);
            if (string.IsNullOrWhiteSpace(token)) error = "Wrong PIN. Try again.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { End(); return; }
        catch (Exception ex) { error = ProfileRepository.ErrorMessage(ex, "Incorrect PIN"); }
        lock (_sync)
        {
            // Cancel while checking must never resurrect a proof or replay the waiting mutation.
            if (!ReferenceEquals(_held, current)) return;
            if (current.Generation != accountGeneration())
            {
                _held = null;
                _reverify = null;
                _answer?.TrySetResult(null);
            }
            else if (error is null)
            {
                _held = current with { Token = token };
                _reverify = null;
                _answer?.TrySetResult(_held);
            }
            else _reverify = new(current.Primary, Error: error);
        }
        Changed?.Invoke();
    }

    internal async Task<T> RunAsync<T>(Func<string, string?, long, Task<T>> call, CancellationToken cancellationToken)
    {
        Held used;
        lock (_sync) used = _held ?? throw Required();
        CheckCurrent(used);
        try
        {
            return await call(used.Primary.Id!, used.Token, used.Generation).ConfigureAwait(false);
        }
        catch (Problem problem) when (NeedsVerification(problem))
        {
            // The first 403 explicitly refused the write. Only this refusal can replay, once;
            // network failures, arbitrary 403s and a second refusal are never auto-retried.
        }
        await _renewLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Held renewed;
        try
        {
            CheckCurrent(used, allowRenewed: true);
            Task<Held?> answer;
            lock (_sync)
            {
                if (!ReferenceEquals(_held, used)) answer = Task.FromResult<Held?>(_held);
                else
                {
                    _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _reverify = new(used.Primary);
                    answer = _answer.Task;
                }
            }
            Changed?.Invoke();
            renewed = await answer.WaitAsync(cancellationToken).ConfigureAwait(false) ?? throw Required();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { End(); throw; }
        finally
        {
            lock (_sync) { _reverify = null; _answer = null; }
            _renewLock.Release();
            Changed?.Invoke();
        }
        CheckCurrent(renewed);
        return await call(renewed.Primary.Id!, renewed.Token, renewed.Generation).ConfigureAwait(false);
    }

    private void CheckCurrent(Held held, bool allowRenewed = false)
    {
        lock (_sync)
        {
            if (_held is null || held.Generation != accountGeneration() || (!allowRenewed && !ReferenceEquals(held, _held)))
                throw Required();
        }
    }

    private static bool NeedsVerification(Problem problem) => problem.ResponseStatusCode == 403
        && problem.Type?.Split('/').LastOrDefault() is "profile_verification_required" or "profile_unverified";
    private static InvalidOperationException Required() => new("Enter the primary profile's PIN to manage profiles.");
}
