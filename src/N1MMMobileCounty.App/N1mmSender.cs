using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    public enum SendStatus
    {
        Sent,
        NotRunning,
        UserBusy,
        DialogNotFound,
        Failed,
    }

    public sealed record SendResult(SendStatus Status, string Message)
    {
        public bool Ok => Status == SendStatus.Sent;
    }

    /// <summary>
    /// Drives N1MM+'s Ctrl+H "Enter the new Rover QTH" prompt: focuses the entry window, presses Ctrl+H,
    /// fills the prompt's text box and clicks OK, then hands focus back to whatever was in front.
    /// To avoid colliding with the operator, it waits for a short pause in keyboard/mouse activity first.
    /// </summary>
    public sealed partial class N1mmSender
    {
        private static readonly Regex EntryWindowTitle = new(@"\bVFO [AB]\b", RegexOptions.Compiled);

        public string ProcessName { get; set; } = "N1MMLogger.net";

        /// <summary>Text that identifies the Ctrl+H prompt (matched against its title and every label in it).</summary>
        public string PromptContains { get; set; } = "Rover QTH";

        /// <summary>Required quiet time (no keys or mouse) before the sender steals focus.</summary>
        public TimeSpan RequiredIdle { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>How long one attempt waits for that quiet time before giving up (the caller retries).</summary>
        public TimeSpan MaxIdleWait { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Only one window-driving operation (Rover QTH prompt or message-editor save) at a time.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<SendResult> SendRoverQthAsync(string county, bool waitForIdle, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await SendRoverQthCoreAsync(county, waitForIdle, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<SendResult> SendRoverQthCoreAsync(string county, bool waitForIdle, CancellationToken cancellationToken)
        {
            var processes = Process.GetProcessesByName(ProcessName);
            if (processes.Length == 0)
            {
                return new SendResult(SendStatus.NotRunning, "not running");
            }

            var pids = new HashSet<uint>();
            var mainWindows = new List<IntPtr>();
            foreach (var p in processes)
            {
                pids.Add((uint)p.Id);
                mainWindows.Add(p.MainWindowHandle);
                p.Dispose();
            }

            Diag.SendStep = "waiting for typing pause";
            if (waitForIdle && !await WaitForIdleAsync(cancellationToken))
            {
                return new SendResult(SendStatus.UserBusy, "Waiting for you to pause typing");
            }

            Diag.SendStep = "finding N1MM+ entry window";
            var entryWindow = FindEntryWindow(pids, mainWindows);
            if (entryWindow == IntPtr.Zero)
            {
                // Usually N1MM+ is still starting up or showing a start-up dialog: keep retrying instead of giving up.
                return new SendResult(SendStatus.NotRunning, "N1MM+ entry window not ready yet (still starting?)");
            }

            var previousForeground = NativeMethods.GetForegroundWindow();
            Diag.SendStep = "bringing N1MM+ to front";
            if (!await BringToFrontAsync(entryWindow, cancellationToken))
            {
                var inFront = NativeMethods.GetText(NativeMethods.GetForegroundWindow());
                return new SendResult(SendStatus.Failed, $"Windows would not let me focus N1MM+ (in front: \"{inFront}\")");
            }

            var hidden = IntPtr.Zero;
            NativeMethods.TimeBeginPeriod(1); // 1 ms timer ticks so the prompt is caught the moment it opens
            try
            {
                var existing = VisibleWindows(pids);
                var prompt = FindPromptDialog(pids, entryWindow);
                if (prompt == null)
                {
                    Diag.SendStep = "pressing Ctrl+H and waiting for the prompt";
                    SendKeyChord(NativeMethods.VkControl, NativeMethods.VkH);
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
                    while (prompt == null && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(1, cancellationToken);

                        // The instant a brand-new N1MM+ window appears, make it see-through so the prompt is never seen.
                        if (hidden == IntPtr.Zero)
                        {
                            hidden = HideNewWindow(pids, existing, entryWindow);
                        }

                        prompt = FindPromptDialog(pids, entryWindow);
                    }
                }

                // Never type blind: if the real prompt (with its edit box and OK button) is not there, do nothing.
                if (prompt == null)
                {
                    return new SendResult(SendStatus.DialogNotFound, "Ctrl+H did not open the Rover QTH prompt");
                }

                var dialog = prompt.Window;
                Diag.SendStep = "filling in the prompt";
                NativeMethods.SendMessageTimeoutText(
                    prompt.Edit, NativeMethods.WmSetText, IntPtr.Zero, county, NativeMethods.SmtoAbortIfHung, 1000, out _);
                NativeMethods.PostMessage(prompt.Ok, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);

                var closed = await WaitForAsync(
                    () => NativeMethods.IsWindow(dialog) && NativeMethods.IsWindowVisible(dialog) ? IntPtr.Zero : new IntPtr(1),
                    TimeSpan.FromSeconds(2.5),
                    cancellationToken);
                if (closed == IntPtr.Zero)
                {
                    return new SendResult(SendStatus.Failed, "The Rover QTH prompt did not close");
                }

                var error = await DismissErrorBoxAsync(pids, existing, dialog, cancellationToken);
                return error != null
                    ? new SendResult(SendStatus.Failed, $"N1MM+ said: {error}")
                    : new SendResult(SendStatus.Sent, $"Sent {county} to N1MM+");
            }
            finally
            {
                if (hidden != IntPtr.Zero && NativeMethods.IsWindow(hidden))
                {
                    SetOpacity(hidden, 255); // if anything went wrong the window must be visible again, never left hidden
                }

                NativeMethods.TimeEndPeriod(1);
                Diag.SendStep = "returning focus to the previous window";
                RestoreForeground(previousForeground, pids);
                Diag.SendStep = "idle";
            }
        }

        private static HashSet<IntPtr> VisibleWindows(HashSet<uint> pids)
        {
            var windows = new HashSet<IntPtr>();
            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (pids.Contains(pid) && NativeMethods.IsWindowVisible(hwnd))
                    {
                        windows.Add(hwnd);
                    }

                    return true;
                },
                IntPtr.Zero);
            return windows;
        }

        /// <summary>Finds an N1MM+ window that was not there before Ctrl+H and makes it fully transparent.</summary>
        private static IntPtr HideNewWindow(HashSet<uint> pids, HashSet<IntPtr> existing, IntPtr entryWindow)
        {
            var created = IntPtr.Zero;
            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (pids.Contains(pid) && hwnd != entryWindow && !existing.Contains(hwnd) && NativeMethods.IsWindowVisible(hwnd))
                    {
                        created = hwnd;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            if (created != IntPtr.Zero)
            {
                SetOpacity(created, 0);
            }

            return created;
        }

        private static void SetOpacity(IntPtr window, byte alpha)
        {
            var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
            if ((style & NativeMethods.WsExLayered) == 0)
            {
                NativeMethods.SetWindowLongPtr(window, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExLayered));
            }

            NativeMethods.SetLayeredWindowAttributes(window, 0, alpha, NativeMethods.LwaAlpha);
        }

        private async Task<bool> WaitForIdleAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + MaxIdleWait;
            while (IdleTime() < RequiredIdle)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }

                await Task.Delay(100, cancellationToken);
            }

            return true;
        }

        private static TimeSpan IdleTime()
        {
            var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var idleMs = unchecked((uint)Environment.TickCount - info.Time);
            return TimeSpan.FromMilliseconds(idleMs);
        }

        private static IntPtr FindEntryWindow(HashSet<uint> pids, List<IntPtr> mainWindows)
        {
            var best = IntPtr.Zero;
            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!pids.Contains(pid) || !NativeMethods.IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    if (EntryWindowTitle.IsMatch(NativeMethods.GetText(hwnd)))
                    {
                        best = hwnd;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            if (best != IntPtr.Zero)
            {
                return best;
            }

            // Title pattern not found: fall back to the process's main window.
            return mainWindows.Find(h => h != IntPtr.Zero);
        }

        private static async Task<bool> BringToFrontAsync(IntPtr window, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ForceForeground(window);
                var ok = await WaitForAsync(
                    () => NativeMethods.GetForegroundWindow() == window ? new IntPtr(1) : IntPtr.Zero,
                    TimeSpan.FromMilliseconds(600),
                    cancellationToken);
                if (ok != IntPtr.Zero)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ForceForeground(IntPtr window)
        {
            if (NativeMethods.IsIconic(window))
            {
                NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            }

            // A synthetic keystroke from this process satisfies Windows' "recent input" rule for
            // SetForegroundWindow. F24 does nothing in any app (unlike Alt, which would wake N1MM+'s menu bar).
            SendKeyChord(NativeMethods.VkF24);

            var thisThread = NativeMethods.GetCurrentThreadId();
            var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
            var attachedForeground = foregroundThread != 0 && foregroundThread != thisThread &&
                NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 && targetThread != thisThread && targetThread != foregroundThread &&
                NativeMethods.AttachThreadInput(thisThread, targetThread, true);
            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(thisThread, targetThread, false);
            }

            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        private static void RestoreForeground(IntPtr previous, HashSet<uint> n1mmPids)
        {
            if (previous == IntPtr.Zero || !NativeMethods.IsWindow(previous))
            {
                return;
            }

            NativeMethods.GetWindowThreadProcessId(previous, out var pid);
            // Leave N1MM+ in front if the click came from our own strip/window; only return focus to other apps.
            if (!n1mmPids.Contains(pid) && pid != (uint)Environment.ProcessId && NativeMethods.GetForegroundWindow() != previous)
            {
                ForceForeground(previous);
            }
        }

        /// <summary>The Ctrl+H input box: its window plus the two controls we drive.</summary>
        private sealed record PromptDialog(IntPtr Window, IntPtr Edit, IntPtr Ok);

        /// <summary>
        /// Finds the real "Enter the new Rover QTH" input box. A window only qualifies if it is not the entry window
        /// (whose title bar also says "Rover QTH: ..."), has a label containing the prompt text, and has both an
        /// edit box and an OK button. Anything less is ignored.
        /// </summary>
        private PromptDialog? FindPromptDialog(HashSet<uint> pids, IntPtr entryWindow)
        {
            PromptDialog? found = null;
            NativeMethods.EnumWindows(
                (hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!pids.Contains(pid) || hwnd == entryWindow || !NativeMethods.IsWindowVisible(hwnd) ||
                        EntryWindowTitle.IsMatch(NativeMethods.GetText(hwnd)))
                    {
                        return true;
                    }

                    var hasPromptText = false;
                    var edit = IntPtr.Zero;
                    var ok = IntPtr.Zero;
                    NativeMethods.EnumChildWindows(
                        hwnd,
                        (child, _) =>
                        {
                            var cls = NativeMethods.GetClass(child);
                            var text = NativeMethods.GetText(child);
                            if (Contains(text, PromptContains))
                            {
                                hasPromptText = true;
                            }

                            if (edit == IntPtr.Zero && Contains(cls, "edit") && NativeMethods.IsWindowVisible(child))
                            {
                                edit = child;
                            }
                            else if (ok == IntPtr.Zero && Contains(cls, "button") &&
                                text.Replace("&", string.Empty).Equals("OK", StringComparison.OrdinalIgnoreCase))
                            {
                                ok = child;
                            }

                            return true;
                        },
                        IntPtr.Zero);

                    if (hasPromptText && edit != IntPtr.Zero && ok != IntPtr.Zero)
                    {
                        found = new PromptDialog(hwnd, edit, ok);
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            return found;
        }

        private static void SendKeyChord(ushort key, ushort? second = null)
        {
            var inputs = new List<NativeMethods.Input> { Key(key, false) };
            if (second.HasValue)
            {
                inputs.Add(Key(second.Value, false));
                inputs.Add(Key(second.Value, true));
            }

            inputs.Add(Key(key, true));
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.Input>());
        }

        private static NativeMethods.Input Key(ushort virtualKey, bool up)
        {
            var input = new NativeMethods.Input { Type = NativeMethods.InputKeyboard };
            input.Union.Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = up ? NativeMethods.KeyEventKeyUp : 0,
            };
            return input;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<IntPtr> WaitForAsync(Func<IntPtr> probe, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var value = probe();
                if (value != IntPtr.Zero || DateTime.UtcNow > deadline)
                {
                    return value;
                }

                await Task.Delay(40, cancellationToken);
            }
        }
    }
}
