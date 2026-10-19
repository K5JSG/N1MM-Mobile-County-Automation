using System;
using System.IO;
using System.Threading;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Tiny diagnostics log (diagnostics.log in the settings folder) for field problems: errors, county changes, send results
    /// and UI stalls. <see cref="Step"/> is a breadcrumb of what the app was doing, so a stall report says where.
    /// </summary>
    internal static class Diag
    {
        private const long MaxBytes = 512 * 1024;

        private static readonly object Gate = new();
        private static readonly string LogPath = AppPaths.File("diagnostics.log");
        private static string _step = "idle";

        private static string _sendStep = "idle";

        /// <summary>What the UI thread was last doing.</summary>
        public static string Step
        {
            get => Volatile.Read(ref _step);
            set => Volatile.Write(ref _step, value);
        }

        /// <summary>What the N1MM+ send (worker thread) was last doing.</summary>
        public static string SendStep
        {
            get => Volatile.Read(ref _sendStep);
            set => Volatile.Write(ref _sendStep, value);
        }

        /// <summary>Starts a fresh log when the old one has grown large, and notes the start of this run.</summary>
        public static void Start()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is a convenience; carry on.
            }

            Log("---- app started ----");
        }

        public static void Log(string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                lock (Gate)
                {
                    File.AppendAllText(LogPath, line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is a convenience; carry on.
            }
        }
    }
}
