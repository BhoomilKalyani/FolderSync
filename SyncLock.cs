namespace FolderSync;

/// <summary>A sync was asked for while another one - in this process or another - was still running.</summary>
public sealed class SyncAlreadyRunningException : Exception
{
    public SyncAlreadyRunningException()
        : base("Another sync is already running. It was left to finish, and nothing was started.") { }
}

/// <summary>
/// Makes sure only one sync runs at a time across every copy of the app: the window's "Sync now" and
/// the Daily, Monthly and Logon tasks. Two overlapping runs over the same pairs race each other - both
/// list what to move aside, then both move and copy - which is how files go missing or get recycled twice.
///
/// A named mutex in the user's session ("Local\"), which is where the scheduled tasks run too. Windows
/// releases it when a process dies, so a crashed run cannot block later ones.
/// </summary>
public static class SyncLock
{
    public const string DefaultName = @"Local\FolderSync.Sync";

    /// <summary>
    /// Runs <paramref name="body"/> while holding the lock. Throws <see cref="SyncAlreadyRunningException"/>
    /// without running it when another holder does not let go within <paramref name="wait"/>.
    ///
    /// Synchronous on purpose: a mutex belongs to the thread that took it, so the work has to finish on
    /// that same thread before it is released.
    /// </summary>
    public static T Run<T>(Func<T> body, TimeSpan wait, string name = DefaultName)
    {
        using var mutex = new Mutex(initiallyOwned: false, name);

        bool owned;
        try { owned = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { owned = true; }   // its holder died mid-sync; ours now

        if (!owned) throw new SyncAlreadyRunningException();
        try { return body(); }
        finally { mutex.ReleaseMutex(); }
    }
}
