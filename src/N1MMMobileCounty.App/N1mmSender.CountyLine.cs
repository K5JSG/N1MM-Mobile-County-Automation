using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// County-line operation. N1MM+'s Ctrl+H Rover QTH prompt only takes one county ("Invalid county, no change made"
    /// for "MON/ORL"). Two counties go through N1MM+'s COUNTYLINE command instead: type COUNTYLINE in the call sign
    /// box, press Enter, and enter the counties separated by commas ("MON,ORL") in the pop-up that opens.
    /// </summary>
    public sealed partial class N1mmSender
    {
        private const ushort VkReturn = 0x0D;

        /// <param name="counties">Comma separated county codes, e.g. "MON,ORL".</param>
        public async Task<SendResult> SendCountyLineAsync(string counties, bool waitForIdle, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await SendCountyLineCoreAsync(counties, waitForIdle, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<SendResult> SendCountyLineCoreAsync(string counties, bool waitForIdle, CancellationToken cancellationToken)
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

            Diag.SendStep = "county line: waiting for typing pause";
            if (waitForIdle && !await WaitForIdleAsync(cancellationToken))
            {
                return new SendResult(SendStatus.UserBusy, "Waiting for you to pause typing");
            }

            var entryWindow = FindEntryWindow(pids, mainWindows);
            if (entryWindow == IntPtr.Zero)
            {
                return new SendResult(SendStatus.NotRunning, "N1MM+ entry window not ready yet (still starting?)");
            }

            // The command is typed into the call sign box, so it must be found by its label and must be empty (never
            // overwrite a call sign that is being entered).
            var callBox = ExchWatcher.FindCallSignBox(entryWindow, out var boxesInRow);
            if (callBox == IntPtr.Zero)
            {
                return new SendResult(SendStatus.Failed, $"Could not find the call sign box (text boxes on the Exch row: {boxesInRow})");
            }

            if (!NativeMethods.TryGetControlText(callBox, out var existingCall) || existingCall.Trim().Length > 0)
            {
                return new SendResult(SendStatus.UserBusy, "Call sign box is not empty - finish the QSO first");
            }

            var previousForeground = NativeMethods.GetForegroundWindow();
            Diag.SendStep = "county line: bringing N1MM+ to front";
            if (!await BringToFrontAsync(entryWindow, cancellationToken))
            {
                return new SendResult(SendStatus.Failed, "Windows would not let me focus N1MM+");
            }

            var existing = VisibleWindows(pids);
            var hidden = IntPtr.Zero;
            NativeMethods.TimeBeginPeriod(1);
            try
            {
                Diag.SendStep = "county line: typing the COUNTYLINE command";
                NativeMethods.SendMessageTimeoutText(callBox, NativeMethods.WmSetText, IntPtr.Zero, "COUNTYLINE", NativeMethods.SmtoAbortIfHung, 1000, out _);
                NativeMethods.PostMessage(callBox, NativeMethods.WmKeyDown, new IntPtr(VkReturn), new IntPtr(0x001C0001));
                NativeMethods.PostMessage(callBox, NativeMethods.WmKeyUp, new IntPtr(VkReturn), new IntPtr(unchecked((int)0xC01C0001)));

                // Wait for the pop-up (an N1MM+ window that was not there before, with a text box, an OK button and the
                // word "count..." in its title or labels), and make it see-through the instant it appears.
                CountyLineDialog? dialog = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (dialog == null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1, cancellationToken);
                    var candidate = HideNewWindow(pids, existing, entryWindow);
                    if (candidate == IntPtr.Zero)
                    {
                        continue;
                    }

                    dialog = DescribeCountyLineDialog(candidate);
                    if (dialog == null)
                    {
                        SetOpacity(candidate, 255); // not the pop-up: show it again and ignore it from now on
                        existing.Add(candidate);
                    }
                    else
                    {
                        hidden = candidate;
                    }
                }

                if (dialog == null)
                {
                    return new SendResult(SendStatus.DialogNotFound, "COUNTYLINE did not open its pop-up");
                }

                Diag.SendStep = "county line: filling in the pop-up";
                NativeMethods.SendMessageTimeoutText(dialog.Edit, NativeMethods.WmSetText, IntPtr.Zero, counties, NativeMethods.SmtoAbortIfHung, 1000, out _);
                NativeMethods.PostMessage(dialog.Ok, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);

                var window = dialog.Window;
                var closed = await WaitForAsync(
                    () => NativeMethods.IsWindow(window) && NativeMethods.IsWindowVisible(window) ? IntPtr.Zero : new IntPtr(1),
                    TimeSpan.FromSeconds(2.5),
                    cancellationToken);
                if (closed == IntPtr.Zero)
                {
                    return new SendResult(SendStatus.Failed, "The COUNTYLINE pop-up did not close");
                }

                var error = await DismissErrorBoxAsync(pids, existing, window, cancellationToken);
                if (error != null)
                {
                    return new SendResult(SendStatus.Failed, $"N1MM+ said: {error}");
                }

                // N1MM+ shows the counties in the entry window's title bar once the county line is set.
                await Task.Delay(300, cancellationToken);
                var title = NativeMethods.GetText(entryWindow);
                foreach (var county in counties.Split(','))
                {
                    if (!title.Contains(county.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return new SendResult(SendStatus.Failed, $"N1MM+'s title does not show {counties} (it says \"{title}\")");
                    }
                }

                return new SendResult(SendStatus.Sent, $"Sent county line {counties} to N1MM+");
            }
            finally
            {
                if (hidden != IntPtr.Zero && NativeMethods.IsWindow(hidden))
                {
                    SetOpacity(hidden, 255); // never leave a window invisible
                }

                // If the command was not accepted, the word must not be left in the call sign box.
                if (NativeMethods.TryGetControlText(callBox, out var left) && left.Trim().Equals("COUNTYLINE", StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.SendMessageTimeoutText(callBox, NativeMethods.WmSetText, IntPtr.Zero, string.Empty, NativeMethods.SmtoAbortIfHung, 1000, out _);
                }

                NativeMethods.TimeEndPeriod(1);
                Diag.SendStep = "returning focus to the previous window";
                RestoreForeground(previousForeground, pids);
                Diag.SendStep = "idle";
            }
        }

        private sealed record CountyLineDialog(IntPtr Window, IntPtr Edit, IntPtr Ok);

        private static CountyLineDialog? DescribeCountyLineDialog(IntPtr window)
        {
            var edit = IntPtr.Zero;
            var ok = IntPtr.Zero;
            var mentionsCounty = Contains(NativeMethods.GetText(window), "count");
            NativeMethods.EnumChildWindows(
                window,
                (child, _) =>
                {
                    var cls = NativeMethods.GetClass(child);
                    var text = NativeMethods.GetText(child);
                    if (Contains(text, "count"))
                    {
                        mentionsCounty = true;
                    }

                    if (edit == IntPtr.Zero && Contains(cls, "edit") && NativeMethods.IsWindowVisible(child))
                    {
                        edit = child;
                    }
                    else if (ok == IntPtr.Zero && Contains(cls, "button") && text.Replace("&", string.Empty).Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        ok = child;
                    }

                    return true;
                },
                IntPtr.Zero);
            return mentionsCounty && edit != IntPtr.Zero && ok != IntPtr.Zero ? new CountyLineDialog(window, edit, ok) : null;
        }

        /// <summary>
        /// After a prompt closes: if N1MM+ throws up a message box ("Invalid county, no change made"), read it, click its
        /// OK so it does not sit on the screen, and return its text. Null when nothing appeared.
        /// </summary>
        private static async Task<string?> DismissErrorBoxAsync(HashSet<uint> pids, HashSet<IntPtr> existing, IntPtr promptWindow, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var window in VisibleWindows(pids))
                {
                    if (existing.Contains(window) || window == promptWindow)
                    {
                        continue;
                    }

                    var parts = new List<string>();
                    var ok = IntPtr.Zero;
                    NativeMethods.EnumChildWindows(
                        window,
                        (child, _) =>
                        {
                            var text = NativeMethods.GetText(child).Replace("&", string.Empty).Trim();
                            if (Contains(NativeMethods.GetClass(child), "button"))
                            {
                                if (ok == IntPtr.Zero && text.Equals("OK", StringComparison.OrdinalIgnoreCase))
                                {
                                    ok = child;
                                }
                            }
                            else if (text.Length > 0)
                            {
                                parts.Add(text);
                            }

                            return true;
                        },
                        IntPtr.Zero);
                    if (ok == IntPtr.Zero)
                    {
                        // Not a message box (e.g. the overlay N1MM+ shows while it loads a contest): not an error, leave it alone.
                        continue;
                    }

                    var message = $"{NativeMethods.GetText(window)}: {string.Join(" ", parts)}".Trim();
                    NativeMethods.PostMessage(ok, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);
                    return message;
                }

                await Task.Delay(40, cancellationToken);
            }

            return null;
        }
    }
}
