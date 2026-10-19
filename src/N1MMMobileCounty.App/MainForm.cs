using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using N1MMMobileCounty.Core;

namespace N1MMMobileCounty.App
{
    public sealed partial class MainForm : Form
    {
        private const int MaxAutoFailures = 5;
        private const double KnotsToMph = 1.15078;
        private const long UiStallMs = 4000;
        private const double CountyLineReachMeters = 3000;
        private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

        private static readonly Color CalmBack = Color.FromArgb(0, 110, 60);
        private static readonly Color FlashBackA = Color.FromArgb(255, 196, 0);
        private static readonly Color FlashBackB = Color.FromArgb(200, 30, 30);
        private static readonly Color UnknownBack = Color.FromArgb(70, 70, 70);

        private readonly AppSettings _settings;
        private CountyLocator _locator = null!; // the open QSO party's counties; swapped by ApplyParty
        private string _partyId = string.Empty;
        private string _partyTitle = string.Empty;
        private string? _detectedParty; // party N1MM+ has open (when it is a supported QSO party)
        private string _detectNote = string.Empty;
        private readonly CountyTracker _tracker;
        private readonly N1mmSender _sender;
        private INmeaSource? _source; // null = no GPS port selected
        private readonly bool _isReplay;
        private CancellationTokenSource? _gpsCts;
        private readonly Dictionary<string, string> _namesByAbbr = new();
        private readonly CancellationTokenSource _cts = new();

        private readonly Panel _banner = new();
        private readonly Label _countyName = new();
        private readonly Label _countyCode = new();
        private readonly Label _previousLabel = new();
        private readonly Label _gpsLabel = new();
        private readonly Label _n1mmLabel = new();
        private readonly CheckBox _autoCheck = new();
        private readonly CheckBox _topCheck = new();
        private readonly Button _sendButton = new();
        private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 350 };
        private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 1000 };

        private string _sourceStatus = "Starting...";
        private DateTime _lastLineAt = DateTime.MinValue;
        private DateTime _lastFixAt = DateTime.MinValue;
        private DateTime _flashUntil = DateTime.MinValue;
        private bool _flashPhase;
        private int _sats = -1;
        private string? _previousCounty;
        private string? _lastSent;
        private int _failures;
        private bool _sending;
        private int _sendTicks;
        private bool _gpsBad;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly object _voiceLock = new();
        private volatile string? _voiceQth;
        private volatile bool _voiceDirty = true; // N1MM+ may still hold an older copy of the messages
        private int _savingEditor;
        private FileSystemWatcher? _voiceWatcher;
        private bool _haveFix;
        private double _lastLat;
        private double _lastLon;
        private string? _lineQth; // e.g. "ALL/CAT" while straddling a county line, otherwise null
        private long _uiBeat = Environment.TickCount64;
        private BarForm? _bar;
        private ExchForm? _exchForm;
        private Icon? _countyIcon;

        public MainForm(AppSettings settings, INmeaSource? source)
        {
            _settings = settings;
            _source = source;
            _isReplay = source is ReplayNmeaSource;
            _tracker = new CountyTracker(settings.ConfirmFixes);
            _sender = new N1mmSender
            {
                ProcessName = settings.N1mmProcessName,
                RequiredIdle = TimeSpan.FromSeconds(settings.IdleSeconds),
            };
            BuildUi();
            ApplyParty(StartingParty());
            if (settings.StartMinimized && settings.ShowBar)
            {
                WindowState = FormWindowState.Minimized;
            }

            Shown += (_, _) =>
            {
                StartBar();
                StartGps();
                StartExchLookup();
                StartVoiceFileWatcher();
                _ = CheckCountyDataAsync(manual: false);
            };
            FormClosing += (_, _) => Shutdown();
        }

        /// <summary>"auto" (follow N1MM+) or the id of the party chosen by hand.</summary>
        public string QsoPartyMode => _settings.QsoParty;

        /// <summary>The party whose counties are in use right now (NY, TX, 7QP...).</summary>
        public string QsoPartyId => _partyId;

        public string QsoPartyTitle => _partyTitle;

        /// <summary>What was seen in N1MM+ when it is not a supported QSO party (empty when fine).</summary>
        public string QsoPartyNote => _detectNote;

        /// <summary>Follow the contest open in N1MM+ ("auto") or use one party regardless.</summary>
        public void SelectQsoParty(string modeOrId)
        {
            var isAuto = modeOrId.Equals("auto", StringComparison.OrdinalIgnoreCase);
            var party = QsoParties.Find(modeOrId);
            if (!isAuto && party == null)
            {
                return;
            }

            _settings.QsoParty = isAuto ? "auto" : party!.Id;
            _settings.Save();
            if (isAuto)
            {
                CheckN1mmParty();
            }
            else
            {
                _detectNote = string.Empty;
                ApplyParty(party!.Id);
            }
        }

        /// <summary>The party to start with: the one chosen by hand, else the one N1MM+ has open, else New York.</summary>
        private string StartingParty()
        {
            if (!_settings.QsoParty.Equals("auto", StringComparison.OrdinalIgnoreCase) && QsoParties.Find(_settings.QsoParty) != null)
            {
                return _settings.QsoParty;
            }

            CheckN1mmParty(apply: false);
            return _detectedParty ?? "NY";
        }

        /// <summary>
        /// Reads N1MM+'s settings file for the open contest. A QSO party we know switches the county list; any other
        /// contest (or a party we have no data for) is noted and the current list stays.
        /// </summary>
        private void CheckN1mmParty(bool apply = true)
        {
            if (!_settings.QsoParty.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var current = N1mmContest.ReadCurrent(_settings.N1mmIni);
            if (current == null)
            {
                return; // N1MM+ never run / file busy: keep what we have
            }

            var (type, sub) = current.Value;
            _detectedParty = null;
            if (!type.Equals("QSOPARTY", StringComparison.OrdinalIgnoreCase))
            {
                _detectNote = $"N1MM+ has a {type} contest open - not a QSO party";
                return;
            }

            // The Indiana party's N1MM+ list also carries the 7QP and New England counties; its own counties are the IN list.
            var party = QsoParties.Find(sub.Equals("IN7QPNE", StringComparison.OrdinalIgnoreCase) ? "IN" : sub);
            if (party == null)
            {
                _detectNote = $"N1MM+ QSO party {sub} - no county data for it";
                return;
            }

            _detectNote = string.Empty;
            _detectedParty = party.Id;
            if (apply && !party.Id.Equals(_partyId, StringComparison.OrdinalIgnoreCase))
            {
                Diag.Log($"N1MM+ contest is now QSOPARTY {sub} -> {party.Id}");
                ApplyParty(party.Id);
            }
        }

        /// <summary>Loads a party's counties and starts over (no committed county until the next few fixes).</summary>
        private void ApplyParty(string id, bool force = false)
        {
            var info = QsoParties.Find(id) ?? QsoParties.Find("NY")!;
            if (!force && _locator != null && info.Id.Equals(_partyId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _locator = QsoParties.Load(info.Id);
            _partyId = info.Id;
            _partyTitle = info.Title;
            _namesByAbbr.Clear();
            foreach (var county in _locator.Counties)
            {
                _namesByAbbr[county.Abbreviation] = county.Name;
            }

            _tracker.Reset();
            _lineQth = null;
            _lastSent = null;
            _previousCounty = null;
            _failures = 0;
            _previousLabel.Text = "Previous county: (none yet)";
            _sendButton.Enabled = false;
            _countyName.Text = _haveFix ? "Finding county..." : "Waiting for GPS...";
            _countyCode.Text = "---";
            UpdateTaskbarIdentity(string.Empty);
            Diag.Log($"QSO party: {info.Id} - {info.Title} ({_locator.CountyCount} counties)");
            RefreshBar();
        }

        /// <summary>"New York", "Maryland-DC", "7th Call Area": the party name without "QSO Party".</summary>
        private string AreaName => _partyTitle.Replace(" QSO Party", string.Empty);

        public bool AutoSwitchEnabled => _autoCheck.Checked;

        public void SetAutoSwitch(bool enabled)
        {
            _autoCheck.Checked = enabled;
        }

        /// <summary>The county-line value being sent (e.g. "ALL/CAT"), or null when operating from a single county.</summary>
        public string? CountyLineQth => _lineQth;

        /// <summary>The committed county, or empty before the first fix / outside New York.</summary>
        public string CurrentCounty => _tracker.Current ?? string.Empty;

        /// <summary>Counties just across a boundary from the current position, nearest first: candidates for a county line.</summary>
        public IReadOnlyList<CountyShape> CountyLineCandidates()
        {
            var current = _tracker.Current;
            if (!_haveFix || string.IsNullOrEmpty(current))
            {
                return Array.Empty<CountyShape>();
            }

            return _locator.FindNearby(_lastLat, _lastLon, current, CountyLineReachMeters, 3);
        }

        /// <summary>
        /// Straddling a county line: sends "CUR/OTHER" to N1MM+ instead of the single county (pass null to go back to
        /// the single county). The line stays in force while the GPS flips between the two counties and ends when the
        /// vehicle really moves on to a different county.
        /// </summary>
        public async Task SetCountyLineAsync(string? partner)
        {
            var current = _tracker.Current;
            if (string.IsNullOrEmpty(current))
            {
                return;
            }

            _lineQth = partner == null
                ? null
                : string.CompareOrdinal(current, partner) < 0 ? $"{current}/{partner}" : $"{partner}/{current}";
            Diag.Log(_lineQth == null ? $"county line off -> {current}" : $"county line on -> {_lineQth}");
            _lastSent = null; // so the auto-sender also retries if the send below is skipped because another send is running
            _flashTimer.Stop();
            ShowCounty(current);
            await ManualSendAsync();
        }

        public bool ExchLookupEnabled => _settings.ExchLookup;

        public void SetExchLookup(bool enabled)
        {
            _settings.ExchLookup = enabled;
            _settings.Save();
            if (!enabled)
            {
                _exchForm?.HideLookup();
            }
        }

        public void ShowDetails()
        {
            WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        public void ExitApp()
        {
            Close();
        }

        /// <summary>The strip was clicked: acknowledge the flash and push the current county into N1MM+ now.</summary>
        public async Task SendFromBarAsync()
        {
            AcknowledgeFlash();
            if (string.IsNullOrEmpty(_tracker.Current))
            {
                _n1mmLabel.Text = "N1MM+: no county to send yet";
                _n1mmLabel.ForeColor = Color.Firebrick;
                RefreshBar();
                return;
            }

            await ManualSendAsync();
        }

        private void StartBar()
        {
            if (!_settings.ShowBar)
            {
                return;
            }

            _bar = new BarForm(this);
            _bar.Show();
            RefreshBar();
        }

        /// <summary>Pushes the current county, colors and status text to the strip.</summary>
        private void RefreshBar()
        {
            if (_bar == null)
            {
                return;
            }

            _bar.Code = _countyCode.Text;
            _bar.CountyName = _countyName.Text;
            var n1mmError = _n1mmLabel.ForeColor == Color.Firebrick;
            var status = _gpsBad ? _gpsLabel.Text : _n1mmLabel.Text;
            _bar.Status = (_gpsBad || n1mmError) ? "! " + status : status;
            _bar.Redraw(_banner.BackColor, _countyName.ForeColor);
        }

        /// <summary>Taskbar button: county code as the icon and in the title, so it is readable at a glance.</summary>
        private void UpdateTaskbarIdentity(string abbr)
        {
            var back = abbr.Length == 0 ? UnknownBack : CalmBack;
            var newIcon = CountyIcon.Create(abbr.Length == 0 ? "--" : abbr, back, Color.White);
            Icon = newIcon;
            _countyIcon?.Dispose();
            _countyIcon = newIcon;
            Text = abbr.Length == 0
                ? $"Outside {AreaName} - N1MM Mobile County Automation"
                : $"{_lineQth ?? abbr} {QthNames(_lineQth ?? abbr)} - {_partyId} - N1MM Mobile County Automation";
        }

        private void BuildUi()
        {
            Text = "N1MM Mobile County Automation";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);
            ClientSize = new Size(520, 430);
            MinimumSize = new Size(420, 400);
            Font = new Font("Segoe UI", 10f);
            TopMost = _settings.AlwaysOnTop;

            _banner.Dock = DockStyle.Top;
            _banner.Height = 200;
            _banner.BackColor = UnknownBack;
            _banner.Cursor = Cursors.Hand;
            _banner.Click += (_, _) => AcknowledgeFlash();

            _countyName.Dock = DockStyle.Top;
            _countyName.Height = 70;
            _countyName.Font = new Font("Segoe UI", 28f, FontStyle.Bold);
            _countyName.ForeColor = Color.White;
            _countyName.TextAlign = ContentAlignment.BottomCenter;
            _countyName.Text = "Waiting for GPS...";
            _countyName.Click += (_, _) => AcknowledgeFlash();

            _countyCode.Dock = DockStyle.Fill;
            _countyCode.Font = new Font("Segoe UI", 54f, FontStyle.Bold);
            _countyCode.ForeColor = Color.White;
            _countyCode.TextAlign = ContentAlignment.MiddleCenter;
            _countyCode.Text = "---";
            _countyCode.Click += (_, _) => AcknowledgeFlash();

            _banner.Controls.Add(_countyCode);
            _banner.Controls.Add(_countyName);
            var logoStream = typeof(MainForm).Assembly.GetManifestResourceStream("logo-256.png");
            if (logoStream != null)
            {
                // Added last so it claims the right-hand edge first; the county name then spans only the space beside it.
                _banner.Controls.Add(new PictureBox
                {
                    Dock = DockStyle.Right,
                    Width = 150,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Image = Image.FromStream(logoStream),
                    BackColor = Color.Transparent,
                    Enabled = false,
                });
            }

            var info = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(14, 10, 14, 10),
            };
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            ConfigureInfoLabel(_previousLabel, "Previous county: (none yet)");
            ConfigureInfoLabel(_gpsLabel, "GPS: starting...");
            ConfigureInfoLabel(_n1mmLabel, "N1MM+: nothing sent yet");

            _autoCheck.Text = "Auto-switch N1MM+ when the county changes";
            _autoCheck.Checked = _settings.AutoSwitch;
            _autoCheck.AutoSize = true;
            _autoCheck.Margin = new Padding(0, 8, 0, 2);
            _autoCheck.CheckedChanged += (_, _) =>
            {
                _settings.AutoSwitch = _autoCheck.Checked;
                _settings.Save();
                _failures = 0;
            };

            _topCheck.Text = "Keep this window on top";
            _topCheck.Checked = _settings.AlwaysOnTop;
            _topCheck.AutoSize = true;
            _topCheck.CheckedChanged += (_, _) =>
            {
                TopMost = _topCheck.Checked;
                _settings.AlwaysOnTop = _topCheck.Checked;
                _settings.Save();
            };

            _sendButton.Text = "Send county to N1MM+ now  (Ctrl+H)";
            _sendButton.Dock = DockStyle.Fill;
            _sendButton.Height = 56;
            _sendButton.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _sendButton.Enabled = false;
            _sendButton.Click += async (_, _) => await ManualSendAsync();

            info.Controls.Add(_previousLabel, 0, 0);
            info.Controls.Add(_gpsLabel, 0, 1);
            info.Controls.Add(_n1mmLabel, 0, 2);
            info.Controls.Add(_autoCheck, 0, 3);
            info.Controls.Add(_topCheck, 0, 4);
            info.Controls.Add(_sendButton, 0, 5);

            Controls.Add(info);
            Controls.Add(_banner);

            _flashTimer.Tick += (_, _) => OnFlashTick();
            _tickTimer.Tick += async (_, _) => await OnTickAsync();
            _tickTimer.Start();
        }

        private static void ConfigureInfoLabel(Label label, string text)
        {
            label.AutoSize = true;
            label.Text = text;
            label.Margin = new Padding(0, 0, 0, 6);
            label.MaximumSize = new Size(480, 0);
        }

        /// <summary>Background thread that notes in diagnostics.log whenever the UI thread stops answering, and where.</summary>
        private void WatchUiThread()
        {
            var stalled = false;
            while (!_cts.Token.WaitHandle.WaitOne(1000))
            {
                var quietMs = Environment.TickCount64 - Interlocked.Read(ref _uiBeat);
                if (quietMs > UiStallMs && !stalled)
                {
                    stalled = true;
                    Diag.Log($"UI thread not responding for {quietMs / 1000}s. UI step: {Diag.Step}. Send step: {Diag.SendStep}");
                }
                else if (quietMs <= UiStallMs && stalled)
                {
                    stalled = false;
                    Diag.Log("UI thread responding again");
                }
            }
        }

        private void StartGps()
        {
            Interlocked.Exchange(ref _uiBeat, Environment.TickCount64);
            new Thread(WatchUiThread) { IsBackground = true, Name = "UI watchdog" }.Start();
            StartGpsSource();
        }

        /// <summary>(Re)starts reading from the current GPS source; the previous one is cancelled first, which closes its port.</summary>
        private void StartGpsSource()
        {
            _gpsCts?.Cancel();
            _gpsCts?.Dispose();
            var source = _source;
            if (source == null)
            {
                _gpsCts = null;
                _sourceStatus = "no GPS port selected (right-click the strip > GPS port)";
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _gpsCts = cts;
            _ = Task.Run(
                () => source.RunAsync(
                    line => { if (!cts.IsCancellationRequested) { BeginInvoke(() => OnNmeaLine(line)); } },
                    status => { if (!cts.IsCancellationRequested) { BeginInvoke(() => _sourceStatus = status); } },
                    cts.Token));
        }

        /// <summary>False for a practice replay, where there is no port to choose.</summary>
        public bool GpsPortSelectable => !_isReplay;

        /// <summary>The selected GPS port, or empty when none is selected (the app then holds no serial port at all).</summary>
        public string GpsPort => _settings.ComPort;

        public int GpsBaud => _settings.BaudRate;

        /// <summary>COM ports Windows currently has (physical and virtual), in port-number order.</summary>
        public IReadOnlyList<string> AvailableGpsPorts()
        {
            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => int.TryParse(name.Substring(Math.Min(3, name.Length)), out var n) ? n : int.MaxValue)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Switches the GPS to another port (empty = stop reading and release the port) and remembers the choice.</summary>
        public void SelectGpsPort(string port)
        {
            _settings.ComPort = port;
            _settings.Save();
            RestartGps($"GPS port -> {(port.Length == 0 ? "none" : port)}");
        }

        public void SelectGpsBaud(int baud)
        {
            _settings.BaudRate = baud;
            _settings.Save();
            RestartGps($"GPS baud rate -> {baud}");
        }

        private void RestartGps(string why)
        {
            if (_isReplay)
            {
                return;
            }

            Diag.Log(why);
            _lastLineAt = DateTime.MinValue;
            _sats = -1;
            _sourceStatus = "Starting...";
            _source = string.IsNullOrWhiteSpace(_settings.ComPort) ? null : new SerialNmeaSource(_settings.ComPort, _settings.BaudRate);
            StartGpsSource();
            RefreshGpsHealth();
        }

        /// <summary>Shows the full county name at the top right while a county code is typed in N1MM+'s Exch box.</summary>
        private void StartExchLookup()
        {
            _exchForm = new ExchForm();
            var watcher = new ExchWatcher(_settings.N1mmProcessName, () => _settings.ExchLookup);
            _ = Task.Run(() => watcher.RunAsync(text => BeginInvoke(() => OnExchText(text)), _cts.Token));
        }

        private void OnExchText(string? text)
        {
            var code = text?.Trim().ToUpperInvariant() ?? string.Empty;
            string? name = null;
            if (_settings.ExchLookup && code.Length > 0 &&
                (_namesByAbbr.TryGetValue(code, out name) ||
                 UsStates.NamesByAbbreviation.TryGetValue(code, out name) ||
                 CanadianProvinces.NamesByAbbreviation.TryGetValue(code, out name)))
            {
                _exchForm?.ShowLookup(code, name);
            }
            else
            {
                _exchForm?.HideLookup();
            }
        }

        private void Shutdown()
        {
            _cts.Cancel();
            _flashTimer.Stop();
            _tickTimer.Stop();
            _voiceWatcher?.Dispose();
            _exchForm?.Close();
            _exchForm = null;
            _bar?.Close(); // also releases the screen space the strip reserved
            _bar = null;
        }

        private void OnNmeaLine(string line)
        {
            _lastLineAt = DateTime.UtcNow;
            var fix = Nmea.ParseFix(line);
            if (fix == null)
            {
                return;
            }

            if (fix.Satellites >= 0)
            {
                _sats = fix.Satellites;
            }

            _lastFixAt = DateTime.UtcNow;
            var county = _locator.Find(fix.Latitude, fix.Longitude);
            _haveFix = true;
            _lastLat = fix.Latitude;
            _lastLon = fix.Longitude;
            _gpsLabel.Text = string.Format(
                CultureInfo.InvariantCulture,
                "GPS: fix{0}  {1:F5}, {2:F5}  {3:F0} mph",
                _sats >= 0 ? $", {_sats} sats" : string.Empty,
                fix.Latitude,
                fix.Longitude,
                fix.SpeedKnots * KnotsToMph);
            _gpsLabel.ForeColor = SystemColors.ControlText;
            _gpsBad = false;

            var previous = _tracker.Current;
            if (_tracker.Update(county?.Abbreviation))
            {
                OnCountyChanged(previous, _tracker.Current ?? string.Empty, fix);
            }
        }

        private void OnCountyChanged(string? previous, string current, GpsFix fix)
        {
            if (_lineQth != null)
            {
                if (Array.IndexOf(_lineQth.Split('/'), current) >= 0)
                {
                    // Still on the line (the GPS just flipped between its two counties): nothing to switch or flash.
                    Diag.Log($"county {(previous ?? "(none)")} -> {current} while on county line {_lineQth}: line kept");
                    ShowCounty(current);
                    return;
                }

                Diag.Log($"left county line {_lineQth}");
                _lineQth = null;
            }

            var firstFix = previous == null;
            if (!firstFix)
            {
                _previousCounty = previous;
            }

            _failures = 0;
            Diag.Step = "county change: update display";
            ShowCounty(current);
            Diag.Step = "county change: write log";
            LogChange(current, fix);
            Diag.Log($"county {(previous ?? "(none)")} -> {(current.Length == 0 ? "OUTSIDE" : current)} at {fix.Latitude:F5},{fix.Longitude:F5}");
            Diag.Step = "county change: update labels";
            _sendButton.Enabled = current.Length > 0;
            if (!string.IsNullOrEmpty(_previousCounty))
            {
                _previousLabel.Text = $"Previous county: {NameAndCode(_previousCounty)}";
            }

            StartFlash(firstFix);
            Diag.Step = "idle";
        }

        private void ShowCounty(string abbr)
        {
            if (abbr.Length == 0)
            {
                _countyName.Text = $"Outside {AreaName}";
                _countyCode.Text = "---";
                _banner.BackColor = UnknownBack;
            }
            else
            {
                var qth = _lineQth ?? abbr;
                _countyName.Text = QthNames(qth);
                _countyCode.Text = qth;
                _banner.BackColor = CalmBack;
            }

            ApplyBannerColors(_banner.BackColor, Color.White);
            UpdateTaskbarIdentity(abbr);
        }

        private void StartFlash(bool quiet)
        {
            _flashUntil = DateTime.Now.AddSeconds(_settings.FlashSeconds);
            _flashTimer.Start();
            if (quiet)
            {
                return;
            }

            if (_settings.Beep)
            {
                // Off the UI thread: a slow or missing audio device must not be able to freeze the strip.
                _ = Task.Run(SystemSounds.Exclamation.Play);
            }

            Diag.Step = "county change: flash taskbar";
            var info = new NativeMethods.FlashInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashInfo>(),
                Hwnd = Handle,
                Flags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG: flash taskbar until the window is focused
            };
            NativeMethods.FlashWindowEx(ref info);
        }

        private void OnFlashTick()
        {
            if (DateTime.Now >= _flashUntil)
            {
                AcknowledgeFlash();
                return;
            }

            _flashPhase = !_flashPhase;
            ApplyBannerColors(
                _flashPhase ? FlashBackA : FlashBackB,
                _flashPhase ? Color.Black : Color.White);
        }

        private void AcknowledgeFlash()
        {
            _flashTimer.Stop();
            var abbr = _tracker.Current;
            _banner.BackColor = string.IsNullOrEmpty(abbr) ? UnknownBack : CalmBack;
            ApplyBannerColors(_banner.BackColor, Color.White);
        }

        private void ApplyBannerColors(Color back, Color fore)
        {
            _banner.BackColor = back;
            _countyName.BackColor = back;
            _countyCode.BackColor = back;
            _countyName.ForeColor = fore;
            _countyCode.ForeColor = fore;
            RefreshBar();
        }

        private bool _n1mmSeen;
        private int _n1mmMissingTicks;

        /// <summary>Closes this program when N1MM+ has been closed for a while (setting ExitWithN1mm); it never exits before N1MM+ has been seen.</summary>
        private void CheckN1mmStillRunning()
        {
            if (!_settings.ExitWithN1mm)
            {
                return;
            }

            // N1MM+ can take a long time to finish exiting after its windows are gone (it saves its database), so a process
            // that is still there but has had no window for a while counts as closed too.
            var processes = Process.GetProcessesByName(_settings.N1mmProcessName);
            var running = processes.Length > 0;
            var hasWindow = false;
            foreach (var p in processes)
            {
                p.Refresh();
                hasWindow |= p.MainWindowHandle != IntPtr.Zero;
                p.Dispose();
            }

            if (running && hasWindow)
            {
                _n1mmSeen = true;
                _n1mmMissingTicks = 0;
            }
            else if (_n1mmSeen && ++_n1mmMissingTicks >= (running ? 8 : 4))
            {
                Diag.Log(running ? "N1MM+ has no window left: exiting" : "N1MM+ has closed: exiting");
                Close();
            }
        }

        private async Task OnTickAsync()
        {
            Interlocked.Exchange(ref _uiBeat, Environment.TickCount64);
            _sendTicks++;
            CheckN1mmStillRunning();

            RefreshGpsHealth();
            if (_sendTicks % 3 == 0)
            {
                CheckN1mmParty();
            }

            // Retry / auto-send every 2 seconds.
            if (_sendTicks % 2 == 0)
            {
                await TrySendAsync(manual: false);
            }
        }

        private void RefreshGpsHealth()
        {
            var now = DateTime.UtcNow;
            if (_lastFixAt != DateTime.MinValue && (now - _lastFixAt).TotalSeconds < 5)
            {
                return;
            }

            string text;
            if (_lastLineAt == DateTime.MinValue || (now - _lastLineAt).TotalSeconds > 5)
            {
                text = $"GPS: no data - {_sourceStatus}";
            }
            else
            {
                text = "GPS: receiving, but no position fix yet (searching for satellites)";
            }

            if (_lastFixAt != DateTime.MinValue)
            {
                text += "  [showing last known county]";
            }

            _gpsLabel.Text = text;
            _gpsLabel.ForeColor = Color.Firebrick;
            _gpsBad = true;
            RefreshBar();
        }

        private async Task ManualSendAsync()
        {
            _failures = 0;
            await TrySendAsync(manual: true);
        }

        private async Task TrySendAsync(bool manual)
        {
            var county = _tracker.Current;
            if (!string.IsNullOrEmpty(county))
            {
                county = _lineQth ?? county;
            }

            if (_sending || string.IsNullOrEmpty(county))
            {
                return;
            }

            if (!manual && (!_autoCheck.Checked || county == _lastSent || _failures >= MaxAutoFailures))
            {
                return;
            }

            _sending = true;
            try
            {
                _n1mmLabel.Text = $"N1MM+: sending {county}...";
                _n1mmLabel.ForeColor = SystemColors.ControlText;
                RefreshBar();
                Diag.Log($"send {county} start ({(manual ? "manual" : "auto")})");

                // Talk to N1MM+ on a worker thread so a stuck window call can never freeze the strip, and give up
                // after a while so one stuck send cannot block every later county.
                var sendTask = Task.Run(() => county.Contains('/')
                    ? _sender.SendCountyLineAsync(county.Replace('/', ','), waitForIdle: !manual, _cts.Token)
                    : _sender.SendRoverQthAsync(county, waitForIdle: !manual, _cts.Token));
                var finished = await Task.WhenAny(sendTask, Task.Delay(SendTimeout, _cts.Token));
                var result = finished == sendTask
                    ? await sendTask
                    : new SendResult(SendStatus.Failed, "N1MM+ did not respond in time");
                Diag.Log($"send {county} -> {result.Status}: {result.Message}");
                if (result.Ok)
                {
                    _lastSent = county;
                    _failures = 0;
                    var qth = county;
                    _ = Task.Run(() => AfterCountySentAsync(qth));
                    _n1mmLabel.Text = $"N1MM+: {county} sent at {DateTime.Now:HH:mm:ss}";
                    _n1mmLabel.ForeColor = Color.DarkGreen;
                    RefreshBar();
                }
                else
                {
                    // N1MM+ may take a while to start (start-up dialogs), so failures in the first minute don't count.
                    var countsAsFailure = result.Status is SendStatus.DialogNotFound or SendStatus.Failed &&
                        DateTime.UtcNow - _startedAt > StartupGrace;
                    if (countsAsFailure)
                    {
                        _failures++;
                    }

                    var giveUp = !manual && _failures >= MaxAutoFailures;
                    _n1mmLabel.Text = giveUp
                        ? $"N1MM+: auto-switch gave up ({result.Message}). Click the bar when ready."
                        : manual
                            ? $"N1MM+: {result.Message}"
                            : $"N1MM+: {result.Message} - will retry";
                    _n1mmLabel.ForeColor = Color.Firebrick;
                    RefreshBar();
                }
            }
            catch (OperationCanceledException)
            {
                // Window is closing.
            }
            catch (Exception ex)
            {
                // Never let a send problem crash the app: show it in the strip and count it as a failure.
                Diag.Log($"send {county} threw: {ex}");
                _failures++;
                _n1mmLabel.Text = $"N1MM+: error - {ex.GetType().Name}: {ex.Message}";
                _n1mmLabel.ForeColor = Color.Firebrick;
                RefreshBar();
            }
            finally
            {
                _sending = false;
            }
        }

        /// <summary>
        /// Rewrites the spoken county in the N1MM+ function-key file to match the Rover QTH just sent. Runs on a worker
        /// thread and never throws: the file is a convenience and must not be able to disturb the county switching.
        /// </summary>
        private bool UpdateVoiceMessages(string qth)
        {
            _voiceQth = qth;
            var changedAny = false;
            var path = _settings.VoiceMessageFile;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // N1MM+ keeps its own copy per operator in a sub-folder (...\W2R\K5JSG\), and that copy is the one it
            // really uses, so every file with the same name at or below the configured file's folder is updated.
            var files = new List<string> { path };
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                {
                    files.AddRange(Directory.GetFiles(dir, Path.GetFileName(path), SearchOption.AllDirectories));
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"voice messages: could not list copies: {ex.GetType().Name}: {ex.Message}");
            }

            foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(file))
                    {
                        lock (_voiceLock)
                        {
                            changedAny |= WriteVoiceFile(file, qth);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log($"voice messages update failed ({file}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            return changedAny;
        }

        /// <summary>After a county reaches N1MM+: rewrite the message files, then make N1MM+ take them in (editor Save).</summary>
        private async Task AfterCountySentAsync(string qth)
        {
            try
            {
                if (UpdateVoiceMessages(qth))
                {
                    _voiceDirty = true;
                }

                var path = _settings.VoiceMessageFile;
                if (!_voiceDirty || !_settings.SaveFunctionKeys || string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    Interlocked.Exchange(ref _savingEditor, 1) == 1)
                {
                    return;
                }

                try
                {
                    // Several tries: the operator may be typing, or N1MM+ may still be starting.
                    for (var attempt = 1; attempt <= 8 && !_cts.IsCancellationRequested; attempt++)
                    {
                        var result = await _sender.SaveSsbMessagesAsync(waitForIdle: true, trace: null, _cts.Token);
                        Diag.Log($"message editor save (try {attempt}) -> {result.Status}: {result.Message}");
                        if (result.Ok)
                        {
                            _voiceDirty = false;
                            return;
                        }

                        if (result.Status == SendStatus.Failed && attempt >= 2)
                        {
                            return; // something is wrong with the editor automation: stop and leave it for the next county
                        }

                        await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _savingEditor, 0);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
            catch (Exception ex)
            {
                Diag.Log($"after-county voice step failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// N1MM+ may save its own (older) copy of the function-key file over ours, putting the placeholder back.
        /// Watch the file and put the current county back a moment after any change.
        /// </summary>
        private void StartVoiceFileWatcher()
        {
            var path = _settings.VoiceMessageFile;
            try
            {
                var dir = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
                if (dir == null || !Directory.Exists(dir))
                {
                    return;
                }

                _voiceWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                };
                FileSystemEventHandler onChange = (_, _) => _ = Task.Run(async () =>
                {
                    await Task.Delay(1500); // let the writer finish; our own rewrite changes nothing the second time round
                    var qth = _voiceQth;
                    if (qth != null)
                    {
                        await AfterCountySentAsync(qth);
                    }
                });
                _voiceWatcher.Changed += onChange;
                _voiceWatcher.Created += onChange;
                _voiceWatcher.Renamed += (_, _) => onChange(this, null!);
                _voiceWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Diag.Log($"voice file watcher not started: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool WriteVoiceFile(string path, string qth)
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var updated = VoiceMessageFile.Apply(text, qth);
            if (updated == text)
            {
                return false;
            }

            var body = new System.Text.UTF8Encoding(false).GetBytes(updated);
            var output = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
            File.WriteAllBytes(path, output);
            Diag.Log($"voice messages -> {qth} ({path})");
            return true;
        }

        /// <summary>"Allegany / Cattaraugus" for "ALL/CAT"; a single code gives just its county name.</summary>
        private string QthNames(string qth)
        {
            var parts = qth.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = _namesByAbbr.TryGetValue(parts[i], out var name) ? name : parts[i];
            }

            return string.Join(" / ", parts);
        }

        private string NameAndCode(string abbr)
        {
            return _namesByAbbr.TryGetValue(abbr, out var name) ? $"{name} ({abbr})" : abbr;
        }

        private static void LogChange(string county, GpsFix fix)
        {
            try
            {
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2:F5},{3:F5}{4}",
                    DateTime.Now,
                    county.Length == 0 ? "OUTSIDE" : county,
                    fix.Latitude,
                    fix.Longitude,
                    Environment.NewLine);
                File.AppendAllText(AppPaths.File("county-changes.log"), line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is a convenience; ignore write problems.
            }
        }
    }
}
