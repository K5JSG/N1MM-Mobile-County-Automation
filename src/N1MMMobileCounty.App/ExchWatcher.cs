using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Watches the Exch text box in N1MM+'s entry window (read-only: it never sends keys, takes focus or changes
    /// anything). The box is found by its "Exch" label, because N1MM+ hands out new control ids on every run.
    /// Runs on a worker thread so a slow N1MM+ can never stall the strip.
    /// </summary>
    internal sealed class ExchWatcher
    {
        private static readonly Regex EntryWindowTitle = new(@"\bVFO [AB]\b", RegexOptions.Compiled);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);

        private readonly string _processName;
        private readonly Func<bool> _isEnabled;
        private IntPtr _edit;
        private DateTime _searchedAt = DateTime.MinValue;

        public ExchWatcher(string processName, Func<bool> isEnabled)
        {
            _processName = processName;
            _isEnabled = isEnabled;
        }

        /// <summary>Calls <paramref name="onText"/> with the Exch text (null = box not available) whenever it changes.</summary>
        public async Task RunAsync(Action<string?> onText, CancellationToken cancellationToken)
        {
            var reported = false;
            string? last = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? text = null;
                if (_isEnabled())
                {
                    try
                    {
                        text = ReadExch();
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"Exch watcher error: {ex.GetType().Name}: {ex.Message}");
                        _edit = IntPtr.Zero;
                    }
                }
                else
                {
                    _edit = IntPtr.Zero;
                }

                if (!reported || text != last)
                {
                    reported = true;
                    last = text;
                    onText(text);
                }

                try
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private string? ReadExch()
        {
            if (_edit == IntPtr.Zero || DateTime.UtcNow - _searchedAt > SearchInterval ||
                !NativeMethods.IsWindow(_edit) || !NativeMethods.IsWindowVisible(_edit))
            {
                var found = FindExchEdit();
                _searchedAt = DateTime.UtcNow;
                if ((found == IntPtr.Zero) != (_edit == IntPtr.Zero))
                {
                    Diag.Log(found == IntPtr.Zero ? "Exch box lost" : "Exch box found");
                }

                _edit = found;
            }

            return _edit != IntPtr.Zero && NativeMethods.TryGetControlText(_edit, out var text) ? text : null;
        }

        private IntPtr FindExchEdit()
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(_processName))
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }

            if (pids.Count == 0)
            {
                return IntPtr.Zero;
            }

            var info = new NativeMethods.GuiThreadInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            NativeMethods.GetGUIThreadInfo(0, ref info);

            // With more than one entry window (two radios) follow the one you are typing in.
            var best = IntPtr.Zero;
            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!pids.Contains(pid) || !NativeMethods.IsWindowVisible(hwnd) || !EntryWindowTitle.IsMatch(NativeMethods.GetText(hwnd)))
                    {
                        return true;
                    }

                    var edit = FindExchEditIn(hwnd);
                    if (edit != IntPtr.Zero && (best == IntPtr.Zero || edit == info.Focus))
                    {
                        best = edit;
                    }

                    return true;
                },
                IntPtr.Zero);
            return best;
        }

        private static IntPtr FindExchEditIn(IntPtr entryWindow) => FindEditBelowLabel(entryWindow, "Exch");

        /// <summary>
        /// The call sign box: the leftmost visible text box on the same row as the Exch box (N1MM+ lays the entry row out
        /// Call, Snt, Rcv, Exch...). Also returns how many boxes were on that row, for the failure message.
        /// </summary>
        internal static IntPtr FindCallSignBox(IntPtr entryWindow, out int boxesInRow)
        {
            boxesInRow = 0;
            var exch = FindEditBelowLabel(entryWindow, "Exch");
            if (exch == IntPtr.Zero || !NativeMethods.GetWindowRect(exch, out var exchRect))
            {
                return IntPtr.Zero;
            }

            var count = 0;
            var best = IntPtr.Zero;
            var bestLeft = int.MaxValue;
            NativeMethods.EnumChildWindows(
                entryWindow,
                (child, _) =>
                {
                    if (NativeMethods.IsWindowVisible(child) &&
                        NativeMethods.GetClass(child).Contains(".EDIT.", StringComparison.OrdinalIgnoreCase) &&
                        NativeMethods.GetWindowRect(child, out var rect) && Math.Abs(rect.Top - exchRect.Top) <= 8)
                    {
                        count++;
                        if (rect.Left < bestLeft)
                        {
                            bestLeft = rect.Left;
                            best = child;
                        }
                    }

                    return true;
                },
                IntPtr.Zero);
            boxesInRow = count;
            return best == exch ? IntPtr.Zero : best; // the Exch box alone is not the call sign box
        }

        /// <summary>The visible text box sitting directly under the visible label with exactly this text.</summary>
        internal static IntPtr FindEditBelowLabel(IntPtr entryWindow, string labelText)
        {
            var labels = new List<NativeMethods.Rect>();
            var edits = new List<(IntPtr Handle, NativeMethods.Rect Rect)>();
            NativeMethods.EnumChildWindows(
                entryWindow,
                (child, _) =>
                {
                    if (!NativeMethods.IsWindowVisible(child) || !NativeMethods.GetWindowRect(child, out var rect))
                    {
                        return true;
                    }

                    var cls = NativeMethods.GetClass(child);
                    if (cls.Contains(".STATIC.", StringComparison.OrdinalIgnoreCase))
                    {
                        if (NativeMethods.GetText(child).Trim().Equals(labelText, StringComparison.OrdinalIgnoreCase))
                        {
                            labels.Add(rect);
                        }
                    }
                    else if (cls.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase))
                    {
                        edits.Add((child, rect));
                    }

                    return true;
                },
                IntPtr.Zero);

            var best = IntPtr.Zero;
            var bestGap = int.MaxValue;
            foreach (var label in labels)
            {
                foreach (var (handle, rect) in edits)
                {
                    var gap = rect.Top - label.Top;
                    if (Math.Abs(rect.Left - label.Left) <= 8 && gap >= -2 && gap <= 40 && gap < bestGap)
                    {
                        best = handle;
                        bestGap = gap;
                    }
                }
            }

            return best;
        }
    }
}
