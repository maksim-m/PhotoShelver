namespace PhotoImporter.Core.Importing;

/// <summary>
/// Retries file operations that fail because another process briefly holds the file
/// (antivirus scanning a freshly written file, the search indexer, a thumbnail generator…).
/// Only sharing/lock violations are retried; every other error is thrown immediately.
/// </summary>
internal static class TransientRetry
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>~3.5 s in total: long enough for an antivirus scan of a large file, short enough not to hang.</summary>
    public static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1000),
        TimeSpan.FromMilliseconds(1000),
    ];

    public static bool IsSharingViolation(Exception ex) =>
        ex is IOException io && (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    public static void Run(Action action, IReadOnlyList<TimeSpan> delays)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < delays.Count)
            {
                Thread.Sleep(delays[attempt]);
            }
        }
    }
}
