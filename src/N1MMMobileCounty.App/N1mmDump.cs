using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Read-only survey of N1MM+'s windows and controls (class, position, current text, which one has the keyboard
    /// focus), used to work out how to find the Exch box. Sends nothing and changes nothing.
    /// </summary>
    internal static class N1mmDump
    {
        public static async Task<int> RunAsync(string processName, string resultFile, int waitSeconds)
        {
            // Time to click into the Exch box and type a code before the snapshot is taken.
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(processName))
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }

            var lines = new List<string> { $"N1MM+ control dump {DateTime.Now:yyyy-MM-dd HH:mm:ss} (process {processName})" };
            if (pids.Count == 0)
            {
                lines.Add("N1MM+ is not running.");
                File.WriteAllLines(resultFile, lines);
                return 1;
            }

            var info = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            NativeMethods.GetGUIThreadInfo(0, ref info); // thread 0 = whichever thread has the foreground
            var foreground = NativeMethods.GetForegroundWindow();
            lines.Add($"Foreground window: {foreground.ToInt64():X}   keyboard focus control: {info.Focus.ToInt64():X}");

            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!pids.Contains(pid) || !NativeMethods.IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    lines.Add(string.Empty);
                    lines.Add($"WINDOW {hwnd.ToInt64():X} title=\"{NativeMethods.GetText(hwnd)}\" class={NativeMethods.GetClass(hwnd)} {Rect(hwnd)}" +
                        (hwnd == foreground ? "  [FOREGROUND]" : string.Empty));
                    var index = 0;
                    NativeMethods.EnumChildWindows(
                        hwnd,
                        (child, _) =>
                        {
                            var focus = child == info.Focus ? "  <== HAS KEYBOARD FOCUS" : string.Empty;
                            lines.Add(
                                $"  #{index++} {child.ToInt64():X} parent={NativeMethods.GetParent(child).ToInt64():X} id={NativeMethods.GetDlgCtrlID(child)} " +
                                $"class={NativeMethods.GetClass(child)} visible={NativeMethods.IsWindowVisible(child)} {Rect(child)} text=\"{ReadText(child)}\"{focus}");
                            return true;
                        },
                        IntPtr.Zero);
                    return true;
                },
                IntPtr.Zero);

            File.WriteAllLines(resultFile, lines);
            return 0;
        }

        private static string Rect(IntPtr hwnd)
        {
            return NativeMethods.GetWindowRect(hwnd, out var r)
                ? $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom})"
                : "rect=?";
        }

        /// <summary>The control's real text via WM_GETTEXT (GetWindowText cannot read edit boxes in another process).</summary>
        private static string ReadText(IntPtr hwnd)
        {
            if (!NativeMethods.TryGetControlText(hwnd, out var raw))
            {
                return "(no answer)";
            }

            var text = raw.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 80 ? text.Substring(0, 80) + "..." : text;
        }
    }
}
