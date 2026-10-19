using System;
using System.IO;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Where the settings file and the logs live. An installed copy (the exe may be in a folder the user cannot write to)
    /// keeps them in %LOCALAPPDATA%\N1MM Mobile County Automation. A portable copy - one with a settings file or a
    /// "portable.txt" beside the exe - keeps them next to the exe, as the first versions of this program did.
    /// </summary>
    internal static class AppPaths
    {
        public const string SettingsName = "N1MMMobileCountyAutomation.ini";
        public const string LegacySettingsName = "NyqpCountyAutomation.ini";

        private static readonly Lazy<string> Dir = new(FindDataDir);

        public static string DataDir => Dir.Value;

        public static string File(string name) => Path.Combine(DataDir, name);

        /// <summary>The settings file in use: the legacy name when an older install already has one, else the current name.</summary>
        public static string SettingsFile
        {
            get
            {
                var legacy = File(LegacySettingsName);
                return System.IO.File.Exists(legacy) && !System.IO.File.Exists(File(SettingsName)) ? legacy : File(SettingsName);
            }
        }

        private static string FindDataDir()
        {
            var exeDir = AppContext.BaseDirectory;
            if (System.IO.File.Exists(Path.Combine(exeDir, SettingsName)) ||
                System.IO.File.Exists(Path.Combine(exeDir, LegacySettingsName)) ||
                System.IO.File.Exists(Path.Combine(exeDir, "portable.txt")))
            {
                return exeDir;
            }

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "N1MM Mobile County Automation");
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return exeDir;
            }
        }
    }
}
