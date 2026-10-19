using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using N1MMMobileCounty.Core;

namespace N1MMMobileCounty.App
{
    /// <summary>County-data updates: the check a little after start-up, the "Update / Not now" question and the install.</summary>
    public sealed partial class MainForm
    {
        private const string DialogCaption = "N1MM Mobile County Automation";
        private static readonly TimeSpan CountyDataCheckDelay = TimeSpan.FromSeconds(20);

        private CountyDataUpdater? _dataUpdater;
        private int _dataCheckRunning;

        /// <summary>Right-click menu: look for county data updates now and say so when there are none.</summary>
        public void CheckCountyDataNow()
        {
            _ = CheckCountyDataAsync(manual: true);
        }

        /// <summary>Right-click menu: drop installed county data and use what the program shipped with.</summary>
        public void UseBuiltInCountyData()
        {
            var answer = MessageBox.Show(
                "Go back to the county data this program came with?\n\nUpdates you installed are removed. You will be offered them again the next time the program starts.",
                DialogCaption,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                return;
            }

            DataUpdater().RemoveInstalled();
            ApplyParty(_partyId, force: true);
            Diag.Log("county data: installed updates removed, using the built-in data");
        }

        private CountyDataUpdater DataUpdater()
        {
            return _dataUpdater ??= new CountyDataUpdater(_settings.CountyDataUrl, AppPaths.File(Program.CountyDataFolderName)) { Log = Diag.Log };
        }

        /// <summary>
        /// At start-up (20 seconds after the window is up, so it never gets in the way of N1MM+ opening) and from the menu: asks the
        /// published list for newer county data. Not being online is not a problem: the program says nothing and carries on.
        /// </summary>
        private async Task CheckCountyDataAsync(bool manual)
        {
            if (!manual && !_settings.CheckCountyData)
            {
                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _dataCheckRunning, 1) == 1)
            {
                return;
            }

            try
            {
                if (!manual)
                {
                    await Task.Delay(CountyDataCheckDelay, _cts.Token);
                }

                var result = await DataUpdater().CheckAsync(_cts.Token);
                if (result.Status == CountyDataUpdater.CheckStatus.Failed)
                {
                    Diag.Log($"county data check failed: {result.Message}");
                    if (manual)
                    {
                        ShowTaskDialog("Could not check for county data updates", "The update list could not be reached. Check the internet connection and try again.", TaskDialogIcon.Warning);
                    }

                    return;
                }

                if (result.Status == CountyDataUpdater.CheckStatus.NeedsNewerProgram)
                {
                    Diag.Log($"county data check: needs a newer program ({result.Message})");
                    if (manual)
                    {
                        ShowTaskDialog("A newer program is needed", "There is county data this version of the program cannot read. Install the latest version of N1MM Mobile County Automation from its GitHub page.", TaskDialogIcon.Information);
                    }

                    return;
                }

                if (result.Updates.Count == 0)
                {
                    Diag.Log("county data check: up to date");
                    if (manual)
                    {
                        ShowTaskDialog("County data is up to date", "You already have the newest county data.", TaskDialogIcon.Information);
                    }

                    return;
                }

                Diag.Log($"county data check: updates for {string.Join(", ", result.Updates.Select(u => $"{u.Id} rev {u.Rev}"))}");
                if (!AskUpdate(result.Updates))
                {
                    Diag.Log("county data: user chose Not now");
                    return;
                }

                var chosen = AskWhichParties(result.Updates);
                if (chosen == null)
                {
                    Diag.Log("county data: user cancelled");
                    return;
                }

                var install = await DataUpdater().InstallAsync(chosen, _cts.Token);
                Diag.Log($"county data installed: {string.Join(" ", install.Installed)}; failed: {string.Join(" | ", install.Failed)}");
                if (install.Installed.Any(id => id.Equals(_partyId, StringComparison.OrdinalIgnoreCase)))
                {
                    ApplyParty(_partyId, force: true);
                }

                ReportInstall(install);
            }
            catch (OperationCanceledException)
            {
                // the program is closing
            }
            catch (Exception ex)
            {
                Diag.Log($"county data update error: {ex}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _dataCheckRunning, 0);
            }
        }

        private static bool AskUpdate(IReadOnlyList<CountyDataEntry> updates)
        {
            var update = new TaskDialogButton("Update");
            var later = new TaskDialogButton("Not now");
            var page = new TaskDialogPage
            {
                Caption = DialogCaption,
                Heading = "County data updates are available",
                Text = $"Newer county data has been published for {Describe(updates)}.\n\nUpdate it now?",
                Icon = TaskDialogIcon.Information,
                Buttons = { update, later },
                DefaultButton = update,
                AllowCancel = true,
                Footnote = new TaskDialogFootnote { Text = "If you choose Not now, you will be asked again the next time the program starts." },
            };
            return TaskDialog.ShowDialog(page) == update;
        }

        /// <summary>
        /// After "Update": all the parties that have updates, or just the QSO party in use. When the party in use has no update there
        /// is nothing to choose between, so every update goes in.
        /// </summary>
        private IReadOnlyList<CountyDataEntry>? AskWhichParties(IReadOnlyList<CountyDataEntry> updates)
        {
            var current = updates.FirstOrDefault(u => u.Id.Equals(_partyId, StringComparison.OrdinalIgnoreCase));
            if (current == null || updates.Count == 1)
            {
                return updates;
            }

            var only = new TaskDialogCommandLinkButton("Just the current QSO party", $"{current.Title}");
            var all = new TaskDialogCommandLinkButton($"All {updates.Count} parties with updates", Describe(updates));
            var page = new TaskDialogPage
            {
                Caption = DialogCaption,
                Heading = "Which county data should be updated?",
                Icon = TaskDialogIcon.Information,
                Buttons = { only, all, TaskDialogButton.Cancel },
                AllowCancel = true,
            };
            var chosen = TaskDialog.ShowDialog(page);
            if (chosen == only)
            {
                return new[] { current };
            }

            return chosen == all ? updates : null;
        }

        private static void ReportInstall(CountyDataUpdater.InstallResult install)
        {
            if (install.Failed.Count == 0)
            {
                ShowTaskDialog("County data updated", $"Updated: {string.Join(", ", install.Installed)}.", TaskDialogIcon.Information);
                return;
            }

            var text = install.Installed.Count > 0 ? $"Updated: {string.Join(", ", install.Installed)}.\n\n" : string.Empty;
            ShowTaskDialog("Some county data could not be updated", text + "Not updated:\n" + string.Join("\n", install.Failed) + "\n\nThe data you had is still in use.", TaskDialogIcon.Warning);
        }

        private static string Describe(IReadOnlyList<CountyDataEntry> updates)
        {
            var names = updates.Take(6).Select(u => u.Title.Replace(" QSO Party", string.Empty)).ToList();
            var more = updates.Count > 6 ? $" and {updates.Count - 6} more" : string.Empty;
            return string.Join(", ", names) + more;
        }

        private static void ShowTaskDialog(string heading, string text, TaskDialogIcon icon)
        {
            TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = DialogCaption,
                Heading = heading,
                Text = text,
                Icon = icon,
                Buttons = { TaskDialogButton.OK },
            });
        }
    }
}
