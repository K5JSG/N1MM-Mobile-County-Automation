using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.App
{
    /// <summary>A source of raw NMEA lines (a serial port, or a recorded file for practice runs).</summary>
    public interface INmeaSource
    {
        string Description { get; }

        /// <summary>Runs until cancelled, calling <paramref name="onLine"/> per sentence and <paramref name="onStatus"/> on connection changes.</summary>
        Task RunAsync(Action<string> onLine, Action<string> onStatus, CancellationToken cancellationToken);
    }

    public sealed class SerialNmeaSource : INmeaSource
    {
        private readonly string _portName;
        private readonly int _baudRate;

        public SerialNmeaSource(string portName, int baudRate)
        {
            _portName = portName;
            _baudRate = baudRate;
        }

        public string Description => $"{_portName} @ {_baudRate}";

        public async Task RunAsync(Action<string> onLine, Action<string> onStatus, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 3000,
                        DtrEnable = true,
                    };
                    port.Open();
                    onStatus($"Connected to {_portName}");
                    using var registration = cancellationToken.Register(() => TryClose(port));
                    await ReadLinesAsync(port, onLine, onStatus, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    onStatus($"{_portName}: {ex.Message}");
                }

                try
                {
                    await Task.Delay(2000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static async Task ReadLinesAsync(SerialPort port, Action<string> onLine, Action<string> onStatus, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            var line = new StringBuilder();
            var stream = port.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken);
                }
                catch (TimeoutException)
                {
                    onStatus("No data from GPS port");
                    continue;
                }

                for (var i = 0; i < read; i++)
                {
                    var ch = (char)buffer[i];
                    if (ch == '\n' || ch == '\r')
                    {
                        if (line.Length > 0)
                        {
                            onLine(line.ToString());
                            line.Clear();
                        }
                    }
                    else if (line.Length < 200)
                    {
                        line.Append(ch);
                    }
                }
            }
        }

        private static void TryClose(SerialPort port)
        {
            try
            {
                port.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Already closing.
            }
        }
    }

    /// <summary>Replays a recorded NMEA file, one second of data per RMC sentence, looping forever.</summary>
    public sealed class ReplayNmeaSource : INmeaSource
    {
        private readonly string _path;
        private readonly int _msPerFix;

        public ReplayNmeaSource(string path, int msPerFix = 1000)
        {
            _path = path;
            _msPerFix = msPerFix;
        }

        public string Description => $"replay of {Path.GetFileName(_path)}";

        public async Task RunAsync(Action<string> onLine, Action<string> onStatus, CancellationToken cancellationToken)
        {
            onStatus($"Replaying {Path.GetFileName(_path)}");
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var line in lines)
                    {
                        onLine(line);
                        if (line.Length > 5 && line.Substring(3, 3) == "RMC")
                        {
                            await Task.Delay(_msPerFix, cancellationToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }
    }
}
