using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using N1MMMobileCounty.Core;

namespace N1MMMobileCounty.App
{
    internal static class Program
    {
        /// <summary>
        /// Usage:
        ///   N1MMMobileCountyAutomation.exe                      normal run (settings from NyqpCountyAutomation.ini)
        ///   N1MMMobileCountyAutomation.exe --replay file.nmea   practice run from a recorded NMEA file
        ///   N1MMMobileCountyAutomation.exe --send-test ALB [resultfile]   push one county to N1MM+ and exit (test aid)
        /// </summary>
        /// <summary>Folder (inside the settings folder) that holds county data installed from an update.</summary>
        internal const string CountyDataFolderName = "county-data";

        [STAThread]
        private static async Task<int> Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            var settings = AppSettings.Load();
            QsoParties.UseOverrideFolder(AppPaths.File(CountyDataFolderName));

            if (args.Length >= 1 && args[0] == "--selfcheck")
            {
                return SelfCheck(args.Length > 1 ? args[1] : null);
            }

            if (args.Length >= 1 && args[0] == "--n1mm-contest")
            {
                // Reads N1MM+'s settings file only (touches no windows): which contest/party it has open and which county list that selects.
                var ini = N1mmContest.FindIni(settings.N1mmIni);
                var current = N1mmContest.ReadCurrent(settings.N1mmIni);
                var party = current == null ? null : QsoParties.Find(current.Value.SubType.Equals("IN7QPNE", StringComparison.OrdinalIgnoreCase) ? "IN" : current.Value.SubType);
                File.WriteAllLines(
                    AppPaths.File("n1mm-contest.txt"),
                    new[]
                    {
                        $"N1MM Logger.ini: {ini ?? "(not found)"}",
                        $"open contest: {(current == null ? "(could not read)" : $"{current.Value.Type} {current.Value.SubType}")}",
                        $"county list: {(party == null ? "(none - the app keeps New York / its last party)" : $"{party.Id} - {party.Title}, {party.CountyCount} counties")}",
                    });
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--send-test")
            {
                return await RunSendTest(settings, args[1], args.Length > 2 ? args[2] : null);
            }

            if (args.Length >= 2 && args[0] == "--send-countyline")
            {
                // Test aid: set a county line in the real N1MM+ without driving, e.g. --send-countyline MON,ORL
                var line = new N1mmSender { ProcessName = settings.N1mmProcessName, RequiredIdle = TimeSpan.FromSeconds(settings.IdleSeconds) };
                var outcome = await line.SendCountyLineAsync(args[1], waitForIdle: true, CancellationToken.None);
                File.WriteAllText(AppPaths.File("countyline-test.txt"), $"{DateTime.Now:HH:mm:ss} {outcome.Status}: {outcome.Message}");
                return (int)outcome.Status;
            }

            if (args.Length >= 1 && args[0] == "--dump-n1mm")
            {
                var file = AppPaths.File("dump-n1mm.txt");
                return await N1mmDump.RunAsync(settings.N1mmProcessName, file, args.Length > 1 ? int.Parse(args[1]) : 12);
            }

            if (args.Length >= 1 && args[0] == "--probe-editor")
            {
                // One-off check against the real N1MM+: right-click an F-key button, save the SSB editor, write what happened.
                var lines = new List<string> { $"editor probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
                var probe = new N1mmSender { ProcessName = settings.N1mmProcessName, RequiredIdle = TimeSpan.FromSeconds(settings.IdleSeconds) };
                var outcome = await probe.SaveSsbMessagesAsync(waitForIdle: true, lines.Add, CancellationToken.None);
                lines.Add($"RESULT: {outcome.Status}: {outcome.Message}");
                File.WriteAllLines(AppPaths.File("editor-probe.txt"), lines);
                return (int)outcome.Status;
            }

            using var mutex = new Mutex(true, "Local\\N1MMMobileCountyAutomation", out var firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("N1MM Mobile County Automation is already running.", "N1MM Mobile County Automation");
                return 1;
            }

            INmeaSource? source = args.Length >= 2 && args[0] == "--replay"
                ? new ReplayNmeaSource(args[1])
                : string.IsNullOrWhiteSpace(settings.ComPort) ? null : new SerialNmeaSource(settings.ComPort, settings.BaudRate);

            Diag.Start();
            if (settings.IsNew && args.Length == 0)
            {
                MessageBox.Show(
                    "Welcome! This program watches your GPS, works out which county you are in, and keeps N1MM+'s Rover QTH up to date.\n\n" +
                    "1. Right-click the small strip at the bottom right of the screen > GPS port, and pick the COM port your GPS receiver uses.\n" +
                    "2. Open your QSO party log in N1MM+. The county list follows the contest N1MM+ has open (right-click the strip > QSO party to pick one by hand).\n\n" +
                    "The strip turns amber/red when the county changes and sends it to N1MM+ automatically.",
                    "N1MM Mobile County Automation",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Diag.Log($"UI thread exception (step: {Diag.Step}): {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Log($"FATAL unhandled exception (step: {Diag.Step}): {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Diag.Log($"Background task exception (step: {Diag.Step}): {e.Exception}");
                e.SetObserved();
            };

            Application.Run(new MainForm(settings, source));
            Diag.Log("app exited normally");
            return 0;
        }

        /// <summary>
        /// Verifies that every Windows API the app imports actually exists (a misspelled entry point only fails
        /// at the moment of the call). Touches no windows. Exit code = number of problems found.
        /// </summary>
        private static int SelfCheck(string? resultFile)
        {
            var problems = new List<string>();
            foreach (var method in typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var import = method.GetCustomAttribute<DllImportAttribute>();
                if (import == null)
                {
                    continue;
                }

                var name = string.IsNullOrEmpty(import.EntryPoint) ? method.Name : import.EntryPoint;
                if (!NativeLibrary.TryLoad(import.Value, out var library))
                {
                    problems.Add($"{import.Value}: library not found");
                }
                else if (!NativeLibrary.TryGetExport(library, name, out _) &&
                    !NativeLibrary.TryGetExport(library, name + "W", out _) &&
                    !NativeLibrary.TryGetExport(library, name + "A", out _))
                {
                    problems.Add($"{import.Value}: entry point '{name}' not found");
                }
            }

            var report = problems.Count == 0 ? "OK: all native imports resolve" : string.Join(Environment.NewLine, problems);
            if (resultFile != null)
            {
                File.WriteAllText(resultFile, report);
            }

            return problems.Count;
        }

        private static async Task<int> RunSendTest(AppSettings settings, string county, string? resultFile)
        {
            var sender = new N1mmSender
            {
                ProcessName = settings.N1mmProcessName,
                RequiredIdle = TimeSpan.Zero,
            };
            var result = await sender.SendRoverQthAsync(county, waitForIdle: false, CancellationToken.None);
            if (resultFile != null)
            {
                File.WriteAllText(resultFile, $"{result.Status}: {result.Message}");
            }

            return (int)result.Status;
        }
    }
}
