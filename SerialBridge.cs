using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Com0com.Redirector
{
    public enum BridgeStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Error,
    }

    /// <summary>
    /// COM Bridge: connects two existing COM ports (COM-A to COM-B and back) through hub4com.
    /// It never creates, removes or renames ports and needs no administrator rights.
    /// Create it on the UI thread; its state changes are marshalled back to that thread.
    /// </summary>
    public class SerialBridge : INotifyPropertyChanged
    {
        /// <summary>
        /// How long hub4com gets to open the ports before the bridge counts as connected.
        /// hub4com exits within about 0.1 s when a port cannot be opened.
        /// </summary>
        public const int StartupCheckMs = 1500;

        private static readonly Regex OpenError = new Regex(@"CreateFile\(""\\\\\.\\(?<port>[^""]+)""\) ERROR (?<code>\d+)");

        private readonly SynchronizationContext _context;
        private readonly string _configuredHub4ComPath;
        private readonly List<string> _hubOutput = new List<string>();
        private Hub4ComProcess _hub;
        private string _leftPort;
        private string _rightPort;
        private string _outputData = "";
        private BridgeStatus _status = BridgeStatus.Disconnected;

        /// <param name="configuredHub4ComPath">Path of hub4com.exe from the settings; hub4com.exe next to this application is used when it does not exist.</param>
        public SerialBridge(string configuredHub4ComPath)
        {
            _configuredHub4ComPath = configuredHub4ComPath;
            _context = SynchronizationContext.Current;
        }

        /// <summary>
        /// A user-facing error message, raised on the UI thread.
        /// </summary>
        public event EventHandler<string> ErrorOccurred;

        #region Properties

        public string LeftPort
        {
            get { return _leftPort; }
            set
            {
                _leftPort = value;
                OnPropertyChanged("LeftPort");
            }
        }

        public string RightPort
        {
            get { return _rightPort; }
            set
            {
                _rightPort = value;
                OnPropertyChanged("RightPort");
            }
        }

        public BridgeStatus Status
        {
            get { return _status; }
            private set
            {
                _status = value;
                OnPropertyChanged("Status");
                OnPropertyChanged("IsIdle");
                OnPropertyChanged("IsActive");
            }
        }

        /// <summary>
        /// No hub4com is running: the ports can be changed and Connect is allowed.
        /// </summary>
        public bool IsIdle
        {
            get { return Status == BridgeStatus.Disconnected || Status == BridgeStatus.Error; }
        }

        /// <summary>
        /// hub4com is running: Disconnect is allowed.
        /// </summary>
        public bool IsActive
        {
            get { return !IsIdle; }
        }

        public string OutputData
        {
            get { return _outputData; }
            private set
            {
                _outputData = value;
                OnPropertyChanged("OutputData");
            }
        }

        #endregion

        public async Task ConnectAsync()
        {
            if (!IsIdle)
            {
                Fail("The bridge is already connected. Disconnect it first.");
                return;
            }

            string left = LeftPort;
            string right = RightPort;
            WriteLog("Connect requested: Left=" + (left ?? "(none)") + ", Right=" + (right ?? "(none)"));

            string error = Validate(left, right);
            if (error != null)
            {
                Fail(error);
                return;
            }

            string checkedPaths;
            string exePath = FindHub4Com(out checkedPaths);
            if (exePath == null)
            {
                Fail("hub4com.exe was not found. Checked: " + checkedPaths);
                return;
            }

            var hub = new Hub4ComProcess(exePath, new[] { left, right });
            hub.OutputReceived += OnHubOutputReceived;
            hub.Exited += OnHubExited;
            _hubOutput.Clear();
            WriteLog("Starting: \"" + exePath + "\" " + hub.Arguments);
            try
            {
                hub.Start();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                Detach(hub);
                hub.Dispose();
                Status = BridgeStatus.Error;
                Fail("Failed to start hub4com: " + ex.Message);
                return;
            }

            _hub = hub;
            WriteLog("hub4com started (PID " + hub.ProcessId + ")"
                + (hub.IsInJob ? "" : " - warning: it will not be stopped automatically if this application crashes"));
            Status = BridgeStatus.Connecting;

            await Task.Delay(StartupCheckMs);

            //an early exit or a Disconnect during the delay has already changed the state
            if (_hub == hub && Status == BridgeStatus.Connecting)
            {
                Status = BridgeStatus.Connected;
                WriteLog("Connected: " + left + " <-> " + right);
            }
        }

        public void Disconnect()
        {
            Hub4ComProcess hub = _hub;
            if (hub == null)
                return;

            WriteLog("Disconnect requested (PID " + hub.ProcessId + ")");
            int? exitCode;
            if (!hub.Stop(Hub4ComProcess.DefaultStopTimeoutMs, out exitCode))
            {
                //keep tracking it so Disconnect (or closing the app) can try again
                Fail("hub4com (PID " + hub.ProcessId + ") did not exit within "
                    + (Hub4ComProcess.DefaultStopTimeoutMs / 1000) + " seconds. The COM ports may still be in use. Try Disconnect again.");
                return;
            }

            _hub = null;
            Detach(hub);
            WriteLog("hub4com exited (exit code " + FormatExitCode(exitCode) + ")");
            Status = BridgeStatus.Disconnected;
        }

        public void WriteLog(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + message;
            Trace.WriteLine("[COM Bridge] " + line);
            OutputData += line + Environment.NewLine;
        }

        private static string Validate(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return "Select both Left COM and Right COM.";
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return "Left COM and Right COM must be different ports.";

            //only ports that Windows reports right now may be passed to hub4com
            List<string> available;
            string error;
            if (!ComPortNames.TryGetSorted(out available, out error))
                return error;
            foreach (string port in new[] { left, right })
            {
                if (!available.Contains(port, StringComparer.OrdinalIgnoreCase))
                    return port + " was not found. Click Refresh and select an existing port.";
            }
            return null;
        }

        private string FindHub4Com(out string checkedPaths)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_configuredHub4ComPath))
                candidates.Add(Environment.ExpandEnvironmentVariables(_configuredHub4ComPath.Trim()));
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hub4com.exe"));

            checkedPaths = string.Join(", ", candidates);
            //only the executable itself: a wrapper script would leave hub4com running when killed
            return candidates.FirstOrDefault(p => File.Exists(p)
                && string.Equals(Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase));
        }

        private void OnHubOutputReceived(object sender, string line)
        {
            Post(() =>
            {
                if (sender != _hub)
                    return;
                _hubOutput.Add(line);
                WriteLog("hub4com: " + line);
            });
        }

        private void OnHubExited(object sender, Hub4ComExitedEventArgs e)
        {
            Post(() => HandleUnexpectedExit((Hub4ComProcess)sender, e.ExitCode));
        }

        private void HandleUnexpectedExit(Hub4ComProcess hub, int? exitCode)
        {
            if (hub != _hub)
                return;
            _hub = null;
            Detach(hub);

            bool wasConnecting = Status == BridgeStatus.Connecting;
            WriteLog("hub4com (PID " + hub.ProcessId + ") exited unexpectedly (exit code " + FormatExitCode(exitCode) + ")");
            Status = BridgeStatus.Error;

            string reason = DescribeOpenFailure(_hubOutput)
                ?? (wasConnecting ? "hub4com could not start the bridge" : "hub4com stopped unexpectedly")
                    + " (exit code " + FormatExitCode(exitCode) + "). See the log for details.";
            Fail(reason);
        }

        /// <summary>
        /// Turn hub4com's 'CreateFile("\\.\COM10") ERROR 5 - ...' output into a readable message.
        /// </summary>
        private static string DescribeOpenFailure(IEnumerable<string> output)
        {
            foreach (string line in output)
            {
                Match match = OpenError.Match(line);
                if (!match.Success)
                    continue;

                string port = match.Groups["port"].Value;
                int code = int.Parse(match.Groups["code"].Value);
                switch (code)
                {
                    case 2:
                    case 3:
                        return "Failed to open " + port + ".\nThe port does not exist.";
                    case 5:
                        return "Failed to open " + port + ".\nThe port may already be in use by another application, or access is denied.";
                    default:
                        return "Failed to open " + port + ".\n" + new Win32Exception(code).Message + " (error " + code + ")";
                }
            }
            return null;
        }

        private void Detach(Hub4ComProcess hub)
        {
            hub.OutputReceived -= OnHubOutputReceived;
            hub.Exited -= OnHubExited;
        }

        private void Fail(string message)
        {
            WriteLog("ERROR: " + message.Replace("\n", " "));
            var handler = ErrorOccurred;
            if (handler != null)
                handler(this, message);
        }

        private void Post(Action action)
        {
            if (_context == null)
                action();
            else
                _context.Post(_ => action(), null);
        }

        private static string FormatExitCode(int? exitCode)
        {
            return exitCode.HasValue ? exitCode.Value.ToString() : "unknown";
        }

        #region INotifyPropertyChangedMembers
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}
