using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// N1MM+ keeps its own copy of the function-key messages and only takes a changed .mc file into that copy when
    /// the SSB Message Editor is opened (right-click an F-key button) and Save is pressed. This does exactly that, with the
    /// editor made see-through the instant it appears, so the spoken county follows the county.
    /// </summary>
    public sealed partial class N1mmSender
    {
        private static readonly Regex SyncStamp = new(@"(?m)^# county automation synced .*\n?", RegexOptions.Compiled);
        private static readonly Regex FunctionKeyLabel = new(@"^F\d{1,2}\s", RegexOptions.Compiled);

        /// <summary>
        /// Opens the SSB Message Editor by right-clicking an F-key button and presses Save. Anything unexpected
        /// (another editor, an extra dialog, a Save that does not close it) leaves the window visible again and is
        /// reported through <paramref name="trace"/>; nothing is ever typed.
        /// </summary>
        public async Task<SendResult> SaveSsbMessagesAsync(bool waitForIdle, Action<string>? trace, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await SaveSsbMessagesCoreAsync(waitForIdle, trace ?? (_ => { }), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<SendResult> SaveSsbMessagesCoreAsync(bool waitForIdle, Action<string> trace, CancellationToken cancellationToken)
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

            if (waitForIdle && !await WaitForIdleAsync(cancellationToken))
            {
                return new SendResult(SendStatus.UserBusy, "Waiting for you to pause typing");
            }

            var entryWindow = FindEntryWindow(pids, mainWindows);
            if (entryWindow == IntPtr.Zero)
            {
                return new SendResult(SendStatus.NotRunning, "N1MM+ entry window not ready yet (still starting?)");
            }

            var button = FindFunctionKeyButton(entryWindow);
            trace($"F-key button: {(button == IntPtr.Zero ? "none found" : $"{button.ToInt64():X} class={NativeMethods.GetClass(button)} text=\"{NativeMethods.GetText(button)}\"")}");
            if (button == IntPtr.Zero)
            {
                return new SendResult(SendStatus.Failed, "Could not find an F-key button to right-click");
            }

            var previousForeground = NativeMethods.GetForegroundWindow();
            var existing = VisibleWindows(pids);
            var hidden = IntPtr.Zero;
            NativeMethods.TimeBeginPeriod(1);
            try
            {
                RightClick(button);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (hidden == IntPtr.Zero && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1, cancellationToken);
                    hidden = HideNewWindow(pids, existing, entryWindow);
                }

                if (hidden == IntPtr.Zero)
                {
                    return new SendResult(SendStatus.Failed, "The message editor did not open");
                }

                // Let the editor finish building its controls.
                var title = NativeMethods.GetText(hidden);
                var save = IntPtr.Zero;
                var cancel = IntPtr.Zero;
                for (var attempt = 0; attempt < 50 && (save == IntPtr.Zero || cancel == IntPtr.Zero); attempt++)
                {
                    FindEditorButtons(hidden, out save, out cancel, attempt == 0 ? trace : null);
                    if (save == IntPtr.Zero || cancel == IntPtr.Zero)
                    {
                        await Task.Delay(40, cancellationToken);
                    }
                }

                trace($"editor window: title=\"{title}\" class={NativeMethods.GetClass(hidden)} save={save.ToInt64():X} cancel={cancel.ToInt64():X}");
                var isSsb = Contains(title, "SSB") && Contains(title, "Editor");
                if (!isSsb || save == IntPtr.Zero)
                {
                    if (cancel != IntPtr.Zero)
                    {
                        NativeMethods.PostMessage(cancel, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);
                    }

                    return new SendResult(SendStatus.Failed, isSsb ? "The editor has no Save button" : $"Opened \"{title}\" instead of the SSB editor; cancelled it");
                }

                var editor = hidden;

                // Save stays greyed out until the text counts as edited, so put the file text into the editor's text box
                // (the same text it loaded), which makes N1MM+ treat it as changed. Nothing about the messages changes.
                var box = FindRichEdit(editor);
                var filePath = EditorFilePath(title);
                trace($"Save enabled before: {NativeMethods.IsWindowEnabled(save)}; text box={box.ToInt64():X}; file={filePath}");
                if (box != IntPtr.Zero && filePath != null && System.IO.File.Exists(filePath))
                {
                    // A comment line at the end that differs every time, so the text really is "changed".
                    var text = SyncStamp.Replace(System.IO.File.ReadAllText(filePath).Replace("\r\n", "\n"), string.Empty).TrimEnd('\n') +
                        $"\n# county automation synced {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                    NativeMethods.SendMessageTimeoutText(box, NativeMethods.WmSetText, IntPtr.Zero, text.Replace("\n", "\r"), NativeMethods.SmtoAbortIfHung, 3000, out _);
                    for (var wait = 0; wait < 15 && !NativeMethods.IsWindowEnabled(save); wait++)
                    {
                        await Task.Delay(40, cancellationToken);
                    }

                    if (!NativeMethods.IsWindowEnabled(save))
                    {
                        // Still greyed: type a space and delete it again inside the editor's text box (posted straight to the box).
                        trace("Save still disabled after setting the text; typing and deleting one space in the text box");
                        NativeMethods.PostMessage(box, NativeMethods.WmChar, new IntPtr(' '), IntPtr.Zero);
                        NativeMethods.PostMessage(box, NativeMethods.WmChar, new IntPtr(8), IntPtr.Zero);
                        for (var wait = 0; wait < 15 && !NativeMethods.IsWindowEnabled(save); wait++)
                        {
                            await Task.Delay(40, cancellationToken);
                        }
                    }
                }

                trace($"Save enabled after: {NativeMethods.IsWindowEnabled(save)}");
                NativeMethods.PostMessage(save, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);

                // Wait for the editor to close; any extra window it throws up (a confirmation box) is reported and left visible.
                var closeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (DateTime.UtcNow < closeDeadline)
                {
                    if (!NativeMethods.IsWindow(editor) || !NativeMethods.IsWindowVisible(editor))
                    {
                        return new SendResult(SendStatus.Sent, "Saved the SSB messages in N1MM+");
                    }

                    await Task.Delay(40, cancellationToken);
                }

                foreach (var window in VisibleWindows(pids))
                {
                    if (!existing.Contains(window) && window != editor)
                    {
                        trace($"extra window after Save: title=\"{NativeMethods.GetText(window)}\" class={NativeMethods.GetClass(window)}");
                        DumpChildren(window, trace);
                    }
                }

                // Never leave the editor sitting open on the operator's screen: back out of it.
                trace($"editor still open after Save (enabled={NativeMethods.IsWindowEnabled(save)}); cancelling it");
                if (cancel != IntPtr.Zero)
                {
                    NativeMethods.PostMessage(cancel, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(800, cancellationToken);
                }

                if (NativeMethods.IsWindow(editor) && NativeMethods.IsWindowVisible(editor))
                {
                    NativeMethods.PostMessage(editor, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                return new SendResult(SendStatus.Failed, "The SSB editor did not close after Save");
            }
            finally
            {
                if (hidden != IntPtr.Zero && NativeMethods.IsWindow(hidden))
                {
                    SetOpacity(hidden, 255); // never leave a window invisible
                }

                NativeMethods.TimeEndPeriod(1);
                RestoreForeground(previousForeground, pids);
            }
        }

        /// <summary>Any visible button-like child of the entry window whose text looks like "F2 Exch".</summary>
        private static IntPtr FindFunctionKeyButton(IntPtr entryWindow)
        {
            var found = IntPtr.Zero;
            NativeMethods.EnumChildWindows(
                entryWindow,
                (child, _) =>
                {
                    if (NativeMethods.IsWindowVisible(child) && FunctionKeyLabel.IsMatch(NativeMethods.GetText(child)) &&
                        Contains(NativeMethods.GetClass(child), "button"))
                    {
                        found = child;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            return found;
        }

        private static void FindEditorButtons(IntPtr editor, out IntPtr save, out IntPtr cancel, Action<string>? trace)
        {
            IntPtr s = IntPtr.Zero, c = IntPtr.Zero;
            NativeMethods.EnumChildWindows(
                editor,
                (child, _) =>
                {
                    var text = NativeMethods.GetText(child).Replace("&", string.Empty).Trim();
                    trace?.Invoke($"  editor child {child.ToInt64():X} class={NativeMethods.GetClass(child)} text=\"{text}\"");
                    if (Contains(NativeMethods.GetClass(child), "button"))
                    {
                        if (s == IntPtr.Zero && text.Equals("Save", StringComparison.OrdinalIgnoreCase))
                        {
                            s = child;
                        }
                        else if (c == IntPtr.Zero && text.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            c = child;
                        }
                    }

                    return true;
                },
                IntPtr.Zero);
            save = s;
            cancel = c;
        }

        private static IntPtr FindRichEdit(IntPtr editor)
        {
            var box = IntPtr.Zero;
            NativeMethods.EnumChildWindows(
                editor,
                (child, _) =>
                {
                    if (Contains(NativeMethods.GetClass(child), "RichEdit"))
                    {
                        box = child;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
            return box;
        }

        /// <summary>The editor's title is "SSB Message Editor - File: C:\...\file.mc".</summary>
        private static string? EditorFilePath(string title)
        {
            var marker = "File:";
            var at = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : title.Substring(at + marker.Length).Trim();
        }

        private static void DumpChildren(IntPtr window, Action<string> trace)
        {
            NativeMethods.EnumChildWindows(
                window,
                (child, _) =>
                {
                    trace($"  child {child.ToInt64():X} class={NativeMethods.GetClass(child)} text=\"{NativeMethods.GetText(child)}\"");
                    return true;
                },
                IntPtr.Zero);
        }

        private static void RightClick(IntPtr control)
        {
            NativeMethods.GetWindowRect(control, out var r);
            var x = Math.Max(1, (r.Right - r.Left) / 2);
            var y = Math.Max(1, (r.Bottom - r.Top) / 2);
            var position = new IntPtr((y << 16) | (x & 0xFFFF));
            NativeMethods.PostMessage(control, NativeMethods.WmRButtonDown, new IntPtr((int)NativeMethods.MkRButton), position);
            NativeMethods.PostMessage(control, NativeMethods.WmRButtonUp, IntPtr.Zero, position);
        }
    }
}
