using Nerdbank.MessagePack;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Windows.Win32;

[assembly: InternalsVisibleTo("procgov-tests")]
[assembly: InternalsVisibleTo("procgov-tests-aot")]

namespace ProcessGovernor.Library;

public static class ProcessGovernorLibraryApi
{
    private static readonly SecurityIdentifier UserIdentifier = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User!;
    public static readonly string DefaultPipeName = Environment.IsPrivilegedProcess ?
        $"procgov-{UserIdentifier.Value}_elevated" : $"procgov-{UserIdentifier.Value}";

    private readonly static TraceSource logger = new("[procgov-lib]");

    private readonly static MessagePackSerializer serializer = new();

    // Serialization

    public static MessagePackSerializer MsgPackSerializer => serializer;

    // Utilities methods

    public static void TryEnablingDebugPrivilege()
    {
        AccountPrivilegeModule.TryEnablingProcessPrivileges(
            PInvoke.GetCurrentProcess_SafeHandle(), ["SeDebugPrivilege"], out _);
    }

    public static Task RunBackgroundAction(Action<CancellationToken> run, CancellationToken ct) =>
        Task.Factory.StartNew(() => run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task RunBackgroundTask(Func<CancellationToken, Task> run, CancellationToken ct) =>
        Task.Factory.StartNew(() => run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    public static bool AreDictionariesEqual<K, V>(IReadOnlyDictionary<K, V> one, IReadOnlyDictionary<K, V> second) where V : notnull =>
        one.Count == second.Count && one.All(kv => second.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));

    // Logging methods

    public static TraceSource Logger => logger;

    public static void SetLibraryLoggerLevel(SourceLevels level) => logger.Switch.Level = level;

    public static void SetLibraryLoggerListeners(TraceListener[] listeners)
    {
        logger.Listeners.Clear();
        logger.Listeners.AddRange(listeners);
    }

    // Extension methods

    public static bool IsCancelledException(this Exception ex) =>
        ex is OperationCanceledException || (ex is AggregateException && ex.InnerException is TaskCanceledException);

    public static void TraceVerbose(this TraceSource logger, string message)
    {
        logger.TraceEvent(TraceEventType.Verbose, 0, message);
    }

    public static void TraceVerbose(this TraceSource logger, string format, params object?[]? args)
    {
        logger.TraceEvent(TraceEventType.Verbose, 0, format, args);
    }

    public static void TraceWarning(this TraceSource logger, string message)
    {
        logger.TraceEvent(TraceEventType.Warning, 0, message);
    }

    public static void TraceWarning(this TraceSource logger, string format, params object?[]? args)
    {
        logger.TraceEvent(TraceEventType.Warning, 0, format, args);
    }

    public static void TraceError(this TraceSource logger, string message)
    {
        logger.TraceEvent(TraceEventType.Error, 0, message);
    }

    public static void TraceError(this TraceSource logger, string format, params object?[]? args)
    {
        logger.TraceEvent(TraceEventType.Error, 0, format, args);
    }
}

// Utility classes

public sealed class SynchronizedPriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private readonly Lock lck = new();
    private readonly PriorityQueue<TElement, TPriority> queue = new();

    /// <summary>
    /// Returns the next element whose priority is smaller than the requested one.
    /// </summary>
    /// <param name="requestedPriority">A priority value to compare with maximum priority in the queue.</param>
    /// <param name="elem">An element that was dequeued if successful.</param>
    /// <returns>True if element with smaller than requested priority was found.</returns>
    public bool TryDequeue(TPriority requestedPriority, [MaybeNullWhen(false)] out TElement elem)
    {
        lock (lck)
        {
            if (queue.TryPeek(out elem, out var maxPriority) && requestedPriority.CompareTo(maxPriority) > 0)
            {
                _ = queue.Dequeue();
                return true;
            }
            return false;
        }
    }

    public void Remove(TElement elem)
    {
        lock (lck) { queue.Remove(elem, out _, out _); }
    }

    public void Enqueue(TElement elem, TPriority priority)
    {
        lock (lck) { queue.Enqueue(elem, priority); }
    }
}


