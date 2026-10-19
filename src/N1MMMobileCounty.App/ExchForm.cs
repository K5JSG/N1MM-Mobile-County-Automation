using System;
using System.Drawing;
using System.Windows.Forms;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Small always-on-top label at the top middle of the screen showing the full county name for the code typed in
    /// N1MM+'s Exch box. It never takes focus, and it is hidden whenever the box does not hold a county code.
    /// </summary>
    internal sealed class ExchForm : Form
    {
        private const int HeightDip = 56;
        private const int PaddingDip = 14;
        private const int MarginDip = 8;

        // A new lookup pulses green three times (green, yellow, green, yellow, green, yellow) and then stays yellow.
        private static readonly Color Yellow = Color.FromArgb(255, 215, 0);
        private static readonly Color Green = Color.FromArgb(50, 205, 50);
        private const int FlashSteps = 6;

        private readonly Font _codeFont = new("Segoe UI", 22f, FontStyle.Bold);
        private readonly Font _nameFont = new("Segoe UI", 22f, FontStyle.Regular);
        private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 250 };
        private string _code = string.Empty;
        private string _name = string.Empty;
        private int _flashStep = FlashSteps;

        public ExchForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Yellow;
            _flashTimer.Tick += (_, _) => OnFlashTick();
        }

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

        /// <summary>Shows "ALB  Albany" (or updates it) at the top middle of the usable screen area.</summary>
        public void ShowLookup(string code, string name)
        {
            var isNew = !Visible || code != _code;
            _code = code;
            _name = name;
            if (isNew)
            {
                _flashStep = 0;
                BackColor = Green;
                _flashTimer.Start();
            }

            var pad = Scale(PaddingDip);
            var codeSize = TextRenderer.MeasureText(code, _codeFont, Size.Empty, TextFormatFlags.NoPadding);
            var nameSize = TextRenderer.MeasureText(name, _nameFont, Size.Empty, TextFormatFlags.NoPadding);
            var width = pad + codeSize.Width + Scale(14) + nameSize.Width + pad;
            var height = Scale(HeightDip);
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            Bounds = new Rectangle(area.Left + ((area.Width - width) / 2), area.Top + Scale(MarginDip), width, height);
            Invalidate();
            if (!Visible)
            {
                Show();
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

        public void HideLookup()
        {
            _flashTimer.Stop();
            if (Visible)
            {
                Hide();
            }
        }

        private void OnFlashTick()
        {
            _flashStep++;
            if (_flashStep >= FlashSteps)
            {
                _flashTimer.Stop();
                BackColor = Yellow;
            }
            else
            {
                BackColor = _flashStep % 2 == 0 ? Green : Yellow;
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var pad = Scale(PaddingDip);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            var codeSize = TextRenderer.MeasureText(g, _code, _codeFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _code, _codeFont, new Rectangle(pad, 0, codeSize.Width, Height), Color.Black, flags);
            var nameLeft = pad + codeSize.Width + Scale(14);
            TextRenderer.DrawText(g, _name, _nameFont, new Rectangle(nameLeft, 0, Width - nameLeft, Height), Color.Black, flags);
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flashTimer.Dispose();
                _codeFont.Dispose();
                _nameFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Scale(int dip)
        {
            return (int)Math.Round(dip * DeviceDpi / 96.0);
        }
    }
}
