using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace N1MMMobileCounty.App
{
    /// <summary>Simple key=value settings file (N1MMMobileCountyAutomation.ini).</summary>
    public sealed class AppSettings
    {
        public static string FilePath => AppPaths.SettingsFile;

        /// <summary>True when there was no settings file yet (a fresh install): the app shows a short welcome once.</summary>
        public bool IsNew { get; private set; }

        public string ComPort { get; set; } = string.Empty;

        public int BaudRate { get; set; } = 4800;

        public bool AutoSwitch { get; set; } = true;

        /// <summary>Consecutive fixes in a new county required before it counts as a county change.</summary>
        public int ConfirmFixes { get; set; } = 3;

        public bool AlwaysOnTop { get; set; } = true;

        public bool Beep { get; set; } = true;

        /// <summary>Show the county strip docked above the taskbar.</summary>
        public bool ShowBar { get; set; } = true;

        /// <summary>Start the detail window minimized to its taskbar button (the strip is the main display).</summary>
        public bool StartMinimized { get; set; } = true;

        public int FlashSeconds { get; set; } = 15;

        /// <summary>Seconds of no keyboard/mouse activity required before the app focuses N1MM+ to switch county.</summary>
        public double IdleSeconds { get; set; } = 1.5;

        public string N1mmProcessName { get; set; } = "N1MMLogger.net";

        /// <summary>Close this program shortly after N1MM+ closes (once N1MM+ has been seen running).</summary>
        public bool ExitWithN1mm { get; set; } = true;

        /// <summary>Show the full county name at the top right while a county code is typed in N1MM+'s Exch box.</summary>
        public bool ExchLookup { get; set; } = true;

        /// <summary>
        /// N1MM+ function-key (.mc) file whose spoken county is kept in step with the Rover QTH. Empty turns it off;
        /// a file that does not exist is simply skipped. OFF by default (the user uses N1MM+'s own {ROVERQTH} and hand-edits the files).
        /// </summary>
        public string VoiceMessageFile { get; set; } = string.Empty;

        /// <summary>
        /// Experimental, OFF by default: after the message file changes, open N1MM+'s SSB Message Editor (hidden) and press Save.
        /// On the real N1MM+ its Save button stays disabled for anything but real typing, so this does not work (2026-10-13 probes).
        /// </summary>
        public bool SaveFunctionKeys { get; set; } = false;

        /// <summary>"auto" = follow the contest N1MM+ has open (from its N1MM Logger.ini); otherwise a party id such as NY, TX, CA, 7QP.</summary>
        public string QsoParty { get; set; } = "auto";

        /// <summary>Full path of N1MM+'s "N1MM Logger.ini" (where it records the open contest). Empty = look in the usual Documents folders.</summary>
        public string N1mmIni { get; set; } = string.Empty;

        /// <summary>Check for newer county data (published on the program's GitHub page) a little after the program starts, and offer it.</summary>
        public bool CheckCountyData { get; set; } = true;

        /// <summary>Where county-data updates come from: an https address or a folder holding manifest.json. Empty = the program's GitHub page.</summary>
        public string CountyDataUrl { get; set; } = string.Empty;

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    settings.IsNew = true;
                    settings.Save();
                    return settings;
                }

                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || eq < 1)
                    {
                        continue;
                    }

                    settings.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch (IOException)
            {
                // Fall back to defaults if the file cannot be read.
            }

            return settings;
        }

        public void Save()
        {
            var lines = new List<string>
            {
                "# N1MM Mobile County Automation settings. Edit while the app is closed.",
                $"ComPort={ComPort}",
                $"BaudRate={BaudRate}",
                $"AutoSwitch={AutoSwitch}",
                $"ConfirmFixes={ConfirmFixes}",
                $"AlwaysOnTop={AlwaysOnTop}",
                $"Beep={Beep}",
                $"ShowBar={ShowBar}",
                $"StartMinimized={StartMinimized}",
                $"FlashSeconds={FlashSeconds}",
                $"IdleSeconds={IdleSeconds.ToString(CultureInfo.InvariantCulture)}",
                $"N1mmProcessName={N1mmProcessName}",
                $"ExitWithN1mm={ExitWithN1mm}",
                $"ExchLookup={ExchLookup}",
                $"VoiceMessageFile={VoiceMessageFile}",
                $"SaveFunctionKeys={SaveFunctionKeys}",
                $"QsoParty={QsoParty}",
                $"N1mmIni={N1mmIni}",
                $"CheckCountyData={CheckCountyData}",
                $"CountyDataUrl={CountyDataUrl}",
            };
            try
            {
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; never fail the app over them.
            }
        }

        private void Apply(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "comport": ComPort = value; break;
                case "baudrate": BaudRate = ParseInt(value, BaudRate); break;
                case "autoswitch": AutoSwitch = ParseBool(value, AutoSwitch); break;
                case "confirmfixes": ConfirmFixes = ParseInt(value, ConfirmFixes); break;
                case "alwaysontop": AlwaysOnTop = ParseBool(value, AlwaysOnTop); break;
                case "beep": Beep = ParseBool(value, Beep); break;
                case "showbar": ShowBar = ParseBool(value, ShowBar); break;
                case "startminimized": StartMinimized = ParseBool(value, StartMinimized); break;
                case "flashseconds": FlashSeconds = ParseInt(value, FlashSeconds); break;
                case "idleseconds":
                    IdleSeconds = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : IdleSeconds;
                    break;
                case "n1mmprocessname": N1mmProcessName = value; break;
                case "exitwithn1mm": ExitWithN1mm = ParseBool(value, ExitWithN1mm); break;
                case "exchlookup": ExchLookup = ParseBool(value, ExchLookup); break;
                case "voicemessagefile": VoiceMessageFile = value; break;
                case "savefunctionkeys": SaveFunctionKeys = ParseBool(value, SaveFunctionKeys); break;
                case "qsoparty": QsoParty = value.Length == 0 ? "auto" : value; break;
                case "n1mmini": N1mmIni = value; break;
                case "checkcountydata": CheckCountyData = ParseBool(value, CheckCountyData); break;
                case "countydataurl": CountyDataUrl = value; break;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return bool.TryParse(value, out var b) ? b : fallback;
        }
    }
}
