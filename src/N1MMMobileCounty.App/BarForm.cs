using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// A compact always-on-top strip sitting at the bottom-right of the screen, just above the taskbar and the clock.
    /// Shows the current county, flashes when it changes, and a click sends the county to N1MM+. It never takes
    /// keyboard focus, so clicking it does not pull you out of N1MM+.
    /// </summary>
    internal sealed class BarForm : Form
    {
        private const int WidthDip = 400;
        private const int HeightDip = 54;
        private const int PaddingDip = 12;
        private const int WmSettingChange = 0x001A;
        private const int WmDisplayChange = 0x007E;

        private readonly MainForm _owner;
        private readonly Font _codeFont = new("Segoe UI", 22f, FontStyle.Bold);
        private readonly Font _nameFont = new("Segoe UI", 13f, FontStyle.Bold);
        private readonly Font _statusFont = new("Segoe UI", 9f, FontStyle.Regular);
        private readonly ToolStripMenuItem _autoItem = new("Auto-switch N1MM+ when the county changes");
        private readonly ToolStripMenuItem _exchItem = new("Show county name when typing in the Exch box");
        private readonly ToolStripMenuItem _partyItem = new("QSO party");
        private readonly ToolStripMenuItem _gpsItem = new("GPS port");
        private readonly ToolStripMenuItem _baudItem = new("GPS baud rate");
        private readonly System.Windows.Forms.Timer _dockTimer = new() { Interval = 1000 };

        public BarForm(MainForm owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(70, 70, 70);

            var menu = new ContextMenuStrip();
            _autoItem.Click += (_, _) => _owner.SetAutoSwitch(!_owner.AutoSwitchEnabled);
            _exchItem.Click += (_, _) => _owner.SetExchLookup(!_owner.ExchLookupEnabled);
            var details = new ToolStripMenuItem("Show details window");
            details.Click += (_, _) => _owner.ShowDetails();
            var dataItem = new ToolStripMenuItem("County data");
            var checkData = new ToolStripMenuItem("Check for county data updates...");
            checkData.Click += (_, _) => _owner.CheckCountyDataNow();
            var builtIn = new ToolStripMenuItem("Use the county data this program came with...");
            builtIn.Click += (_, _) => _owner.UseBuiltInCountyData();
            dataItem.DropDownItems.AddRange(new ToolStripItem[] { checkData, builtIn });
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (_, _) => _owner.ExitApp();
            menu.Items.AddRange(new ToolStripItem[] { _autoItem, _exchItem, _partyItem, _gpsItem, _baudItem, dataItem, details, new ToolStripSeparator(), exit });
            menu.Opening += (_, _) =>
            {
                _autoItem.Checked = _owner.AutoSwitchEnabled;
                _exchItem.Checked = _owner.ExchLookupEnabled;
                BuildCountyLineItems(menu);
                BuildPartyItems();
                BuildGpsItems();
            };
            ContextMenuStrip = menu;

            // Every second: re-dock in case the taskbar moved or resized, and climb back to the top of the
            // topmost band in case the taskbar, Start menu or another topmost window has been shown over us.
            _dockTimer.Tick += (_, _) =>
            {
                PlaceAboveTaskbar();
                KeepOnTop();
            };
        }

        /// <summary>
        /// "QSO party": follow the contest open in N1MM+ (the default) or pick a party by hand. The parties are grouped
        /// alphabetically so the menu stays short enough for a small screen.
        /// </summary>
        private void BuildPartyItems()
        {
            _partyItem.DropDownItems.Clear();
            var mode = _owner.QsoPartyMode;
            var auto = mode.Equals("auto", StringComparison.OrdinalIgnoreCase);
            _partyItem.Text = $"QSO party: {_owner.QsoPartyTitle}";
            var follow = new ToolStripMenuItem($"Follow N1MM+ (now {_owner.QsoPartyId})") { Checked = auto };
            follow.Click += (_, _) => _owner.SelectQsoParty("auto");
            _partyItem.DropDownItems.Add(follow);
            if (_owner.QsoPartyNote.Length > 0)
            {
                _partyItem.DropDownItems.Add(new ToolStripMenuItem(_owner.QsoPartyNote) { Enabled = false });
            }

            _partyItem.DropDownItems.Add(new ToolStripSeparator());
            var parties = N1MMMobileCounty.Core.QsoParties.All.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
            const int perGroup = 12;
            for (var start = 0; start < parties.Count; start += perGroup)
            {
                var chunk = parties.Skip(start).Take(perGroup).ToList();
                var group = new ToolStripMenuItem($"{chunk[0].Title.Split(' ')[0]} - {chunk[chunk.Count - 1].Title.Split(' ')[0]}");
                foreach (var party in chunk)
                {
                    var item = new ToolStripMenuItem($"{party.Title}  ({party.Id})")
                    {
                        Checked = !auto && party.Id.Equals(mode, StringComparison.OrdinalIgnoreCase),
                    };
                    var chosen = party.Id;
                    item.Click += (_, _) => _owner.SelectQsoParty(chosen);
                    group.DropDownItems.Add(item);
                }

                group.Checked = group.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Checked);
                _partyItem.DropDownItems.Add(group);
            }
        }

        /// <summary>
        /// "GPS port" lists the COM ports Windows has right now (tick = in use) plus "None", which releases the port so
        /// another program can have it. "GPS baud rate" sets the speed. Both take effect at once and are remembered.
        /// </summary>
        private void BuildGpsItems()
        {
            _gpsItem.DropDownItems.Clear();
            _baudItem.DropDownItems.Clear();
            _gpsItem.Enabled = _baudItem.Enabled = _owner.GpsPortSelectable;
            if (!_owner.GpsPortSelectable)
            {
                return;
            }

            var current = _owner.GpsPort;
            var ports = new List<string>(_owner.AvailableGpsPorts());
            if (current.Length > 0 && !ports.Any(p => p.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                ports.Add(current); // the chosen port is unplugged: still show it
            }

            foreach (var port in ports)
            {
                var item = new ToolStripMenuItem(port) { Checked = port.Equals(current, StringComparison.OrdinalIgnoreCase) };
                var chosen = port;
                item.Click += (_, _) => _owner.SelectGpsPort(chosen);
                _gpsItem.DropDownItems.Add(item);
            }

            if (ports.Count == 0)
            {
                _gpsItem.DropDownItems.Add(new ToolStripMenuItem("(no COM ports found)") { Enabled = false });
            }

            _gpsItem.DropDownItems.Add(new ToolStripSeparator());
            var none = new ToolStripMenuItem("None - don't read a GPS (releases the port)") { Checked = current.Length == 0 };
            none.Click += (_, _) => _owner.SelectGpsPort(string.Empty);
            _gpsItem.DropDownItems.Add(none);

            foreach (var baud in new[] { 4800, 9600, 19200, 38400, 57600, 115200 })
            {
                var item = new ToolStripMenuItem(baud.ToString()) { Checked = baud == _owner.GpsBaud };
                var chosen = baud;
                item.Click += (_, _) => _owner.SelectGpsBaud(chosen);
                _baudItem.DropDownItems.Add(item);
            }
        }

        /// <summary>Top of the right-click menu: switch to "CUR/OTHER" when straddling a county line, or back to one county.</summary>
        private void BuildCountyLineItems(ContextMenuStrip menu)
        {
            for (var i = menu.Items.Count - 1; i >= 0; i--)
            {
                if (menu.Items[i].Tag is CountyLineTag)
                {
                    var old = menu.Items[i];
                    menu.Items.RemoveAt(i);
                    old.Dispose();
                }
            }

            var items = new List<ToolStripItem>();
            var current = _owner.CurrentCounty;
            if (_owner.CountyLineQth is { } line)
            {
                var back = new ToolStripMenuItem($"Leave county line {line}: just {current}") { Tag = new CountyLineTag() };
                back.Click += (_, _) => _ = _owner.SetCountyLineAsync(null);
                items.Add(back);
            }
            else
            {
                var candidates = _owner.CountyLineCandidates();
                foreach (var other in candidates)
                {
                    var partner = other.Abbreviation;
                    var pair = string.CompareOrdinal(current, partner) < 0 ? $"{current}/{partner}" : $"{partner}/{current}";
                    var item = new ToolStripMenuItem($"On the county line: switch to {pair}") { Tag = new CountyLineTag() };
                    item.Click += (_, _) => _ = _owner.SetCountyLineAsync(partner);
                    items.Add(item);
                }

                if (candidates.Count == 0)
                {
                    items.Add(new ToolStripMenuItem("County line (no other county within 3 km)") { Enabled = false, Tag = new CountyLineTag() });
                }
            }

            items.Add(new ToolStripSeparator { Tag = new CountyLineTag() });
            for (var i = 0; i < items.Count; i++)
            {
                menu.Items.Insert(i, items[i]);
            }
        }

        private sealed class CountyLineTag
        {
        }

        public string Code { get; set; } = "---";

        public string CountyName { get; set; } = "Waiting for GPS...";

        public string Status { get; set; } = string.Empty;

        public Color Foreground { get; set; } = Color.White;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
                return cp;
            }
        }

        public void Redraw(Color back, Color fore)
        {
            BackColor = back;
            Foreground = fore;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PlaceAboveTaskbar();
            KeepOnTop();
            _dockTimer.Start();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _dockTimer.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmSettingChange || m.Msg == WmDisplayChange)
            {
                PlaceAboveTaskbar(); // work area changed (taskbar moved/resized) or the display layout changed
                KeepOnTop();
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _ = _owner.SendFromBarAsync();
            }

            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var pad = Scale(PaddingDip);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            var dim = Color.FromArgb(
                (Foreground.R * 3 / 4) + (BackColor.R / 4),
                (Foreground.G * 3 / 4) + (BackColor.G / 4),
                (Foreground.B * 3 / 4) + (BackColor.B / 4));

            var codeSize = TextRenderer.MeasureText(g, Code, _codeFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Code, _codeFont, new Rectangle(pad, 0, codeSize.Width, Height), Foreground, flags);

            var textLeft = pad + codeSize.Width + Scale(12);
            var textWidth = Math.Max(Scale(40), Width - textLeft - pad);
            var half = Height / 2;
            TextRenderer.DrawText(g, CountyName, _nameFont, new Rectangle(textLeft, 2, textWidth, half), Foreground, flags);
            TextRenderer.DrawText(g, Status, _statusFont, new Rectangle(textLeft, half, textWidth, half - 3), dim, flags);
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dockTimer.Dispose();
                _codeFont.Dispose();
                _nameFont.Dispose();
                _statusFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Scale(int dip)
        {
            return (int)Math.Round(dip * DeviceDpi / 96.0);
        }

        /// <summary>Puts the strip at the very top of the topmost band, without activating it or moving it.</summary>
        private void KeepOnTop()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        /// <summary>Bottom-right corner of the usable screen area: directly above the taskbar, over the clock.</summary>
        private void PlaceAboveTaskbar()
        {
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            var width = Scale(WidthDip);
            var height = Scale(HeightDip);
            var bounds = new Rectangle(area.Right - width, area.Bottom - height, width, height);
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }
    }
}
