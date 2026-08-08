using System.Diagnostics.CodeAnalysis;

namespace Fonoteca.Api.Library;

/// <summary>
/// One piece of library-wide work at a time.
/// </summary>
/// <remarks>
/// A scan and an identification pass must not overlap, and the reason is
/// specific rather than general tidiness: a scan that decides a file changed
/// clears every derived column on it — fingerprint, AcoustID, the lot — while
/// the identification pass may be part-way through filling those in for the same
/// row. The pass would then finish, write its result, and be silently
/// contradicted.
///
/// One gate rather than two services consulting each other's <c>IsRunning</c>:
/// that arrangement needs each to hold a reference to the other, which is a
/// circular dependency the container refuses at startup. It is also the honest
/// model — the resource being protected is the catalogue, not either service.
///
/// A lease rather than a flag with a matching release call, so the only way to
/// hold the gate is inside a <c>using</c> and the only way to leak it is to go
/// out of your way.
/// </remarks>
public sealed class LibraryWorkGate
{
    private readonly Lock _sync = new();

    private string? _activeKind;

    /// <summary>What holds the gate, or null when nothing does.</summary>
    public string? ActiveKind
    {
        get
        {
            lock (_sync) return _activeKind;
        }
    }

    public bool IsBusy => ActiveKind is not null;

    /// <summary>
    /// Takes the gate, or reports who has it.
    /// </summary>
    /// <remarks>
    /// Never waits. A second request arriving mid-pass is a mistake to report
    /// back, not a queue to join — the caller wants to be told a scan is already
    /// running, not blocked for forty minutes while one finishes.
    /// </remarks>
    public bool TryEnter(string kind, [NotNullWhen(true)] out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        lock (_sync)
        {
            if (_activeKind is not null)
            {
                lease = null;
                return false;
            }

            _activeKind = kind;
        }

        lease = new Lease(this);
        return true;
    }

    private void Release()
    {
        lock (_sync) _activeKind = null;
    }

    /// <summary>Idempotent, so a double dispose cannot release somebody else's turn.</summary>
    private sealed class Lease(LibraryWorkGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
