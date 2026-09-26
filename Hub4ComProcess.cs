using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Com0com.Redirector
{
    public class Hub4ComExitedEventArgs : EventArgs
    {
        public Hub4ComExitedEventArgs(int? exitCode)
        {
            ExitCode = exitCode;
        }

        public int? ExitCode { get; private set; }
    }

    /// <summary>
    /// One hub4com.exe process started by this application. It only ever stops the
    /// process it started itself - never other hub4com instances on the machine.
    /// An instance is single use: create a new one for every connection.
    /// </summary>
    public sealed class Hub4ComProcess : IDisposable
    {
        public const int DefaultStopTimeoutMs = 5000;

        private readonly object _sync = new object();
        private readonly string _exePath;
        private Process _process;
        private bool _started;

        /// <summary>
        /// A line written by hub4com to stdout or stderr. Raised on a worker thread.
        /// </summary>
        public event EventHandler<string> OutputReceived;

        /// <summary>
        /// hub4com exited without Stop() being called. Raised on a worker thread after
        /// all of its output has been delivered through OutputReceived.
        /// </summary>
        public event EventHandler<Hub4ComExitedEventArgs> Exited;

        public Hub4ComProcess(string exePath, IList<string> portNames)
        {
            _exePath = exePath;
            Arguments = BuildArguments(portNames);
        }

        public string Arguments { get; private set; }
        public int ProcessId { get; private set; }

        /// <summary>
        /// True if the process will also be terminated when this application exits abnormally.
        /// </summary>
        public bool IsInJob { get; private set; }

        /// <summary>
        /// Build the hub4com arguments that route data received on any port to all the
        /// other ports (for two ports: COM-A to COM-B and COM-B to COM-A).
        /// --octs=off disables hub4com's default CTS output handshaking, which would
        /// otherwise stall output until the device or application on the other end raises RTS.
        /// </summary>
        public static string BuildArguments(IList<string> portNames)
        {
            if (portNames == null || portNames.Count < 2)
                throw new ArgumentException("At least two ports are required.", "portNames");

            var arguments = new StringBuilder("--octs=off --route=All:All");
            foreach (string name in portNames)
            {
                if (!ComPortNames.IsSafeName(name))
                    throw new ArgumentException("Invalid COM port name: " + name, "portNames");
                arguments.Append(@" \\.\").Append(name);
            }
            return arguments.ToString();
        }

        public void Start()
        {
            //holding the lock until the process is stored makes an immediate exit wait in OnExited
            lock (_sync)
            {
                if (_started)
                    throw new InvalidOperationException("This hub4com process has already been started.");
                _started = true;

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _exePath,
                        Arguments = Arguments,
                        WorkingDirectory = Path.GetDirectoryName(_exePath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                process.OutputDataReceived += OnDataReceived;
                process.ErrorDataReceived += OnDataReceived;
                process.Exited += OnExited;

                try
                {
                    process.Start();
                }
                catch
                {
                    process.Dispose();
                    throw;
                }

                _process = process;
                ProcessId = process.Id;
                IsInJob = ChildProcessJob.TryAssign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
        }

        /// <summary>
        /// Terminate our hub4com process, wait for it to exit and release it.
        /// Returns false if it did not exit within the timeout; the process is then kept so Stop can be retried.
        /// </summary>
        public bool Stop(int timeoutMs, out int? exitCode)
        {
            exitCode = null;
            Process process;
            lock (_sync)
            {
                process = _process;
                _process = null;
            }
            if (process == null)
                return true;

            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                //already exited
            }
            catch (Win32Exception)
            {
                //already exiting
            }

            if (!process.WaitForExit(timeoutMs))
            {
                lock (_sync)
                {
                    _process = process;
                }
                return false;
            }
            exitCode = WaitAndGetExitCode(process);
            process.Dispose();
            return true;
        }

        public void Dispose()
        {
            int? exitCode;
            Stop(DefaultStopTimeoutMs, out exitCode);
        }

        private void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            var handler = OutputReceived;
            if (handler != null)
                handler(this, e.Data);
        }

        private void OnExited(object sender, EventArgs e)
        {
            int? exitCode;
            lock (_sync)
            {
                //null means Stop() took the process and handles the exit itself
                if (_process == null)
                    return;
                exitCode = WaitAndGetExitCode(_process);
                _process.Dispose();
                _process = null;
            }

            var handler = Exited;
            if (handler != null)
                handler(this, new Hub4ComExitedEventArgs(exitCode));
        }

        private static int? WaitAndGetExitCode(Process process)
        {
            try
            {
                //the parameterless overload also waits for the redirected output to be drained
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
