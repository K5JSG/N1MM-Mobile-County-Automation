using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace N1MMMobileCounty.App
{
    /// <summary>Draws the county code onto a small icon so the taskbar button itself shows where you are.</summary>
    internal static class CountyIcon
    {
        public static Icon Create(string code, Color back, Color fore)
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var brush = new SolidBrush(back);
                g.FillRectangle(brush, 0, 0, 32, 32);
                using var font = new Font("Segoe UI", code.Length > 2 ? 12f : 16f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(fore);
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(code, font, textBrush, new RectangleF(0, 0, 32, 32), format);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }
}
