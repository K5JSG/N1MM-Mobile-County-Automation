using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace FakeN1mm
{
    /// <summary>
    /// Test stand-in for the N1MM+ entry window: same title pattern, and Ctrl+H opens the same
    /// "Enter the new Rover QTH." input box. The value entered is appended to the file given as argument 1.
    /// </summary>
    internal sealed class FakeEntryWindow : Form
    {
        private readonly string _resultFile;

        public FakeEntryWindow(string resultFile)
        {
            _resultFile = resultFile;
            Text = "7000.00 LSB Manual - VFO A  Rover QTH: MON  Op: W2R"; // mirrors the real title bar (it also says "Rover QTH")
            KeyPreview = true;
            Width = 600;
            Height = 200;
            Controls.Add(new TextBox { Left = 20, Top = 20, Width = 300 });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.H)
            {
                e.SuppressKeyPress = true;
                var value = Interaction.InputBox("Enter the new Rover QTH.", "N1MMLogger.net", string.Empty);
                File.AppendAllText(_resultFile, "[" + value + "]" + Environment.NewLine);
            }

            base.OnKeyDown(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new FakeEntryWindow(args.Length > 0 ? args[0] : "fake-n1mm-result.txt"));
        }
    }
}
