using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Com0com.Redirector
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public ObservableCollection<Com0comPortPair> PortPairs { get; set; }

        public SerialBridge Bridge { get; private set; }
        public ObservableCollection<string> ComPorts { get; private set; }
        public ObservableCollection<BridgePreset> BridgePresets { get; private set; }

        public MainWindow()
        {
            Bridge = new SerialBridge(Properties.Settings.Default.Hub4ComPath);
            Bridge.ErrorOccurred += Bridge_ErrorOccurred;
            ComPorts = new ObservableCollection<string>();
            BridgePresets = new ObservableCollection<BridgePreset>();
            try
            {
                PortPairs = Com0comSetup.GetPortPairs();
            }
            catch
            { 
                //setupc.exe requires administrator rights; the COM Bridge works without them
                PortPairs = new ObservableCollection<Com0comPortPair>();
                Bridge.WriteLog("Unable to list com0com port pairs (setupc.exe requires administrator rights, or com0com is not installed). Port pair redirection is unavailable; COM Bridge can still be used.");
            }
            InitializeComponent();
            cboCommsMode.ItemsSource = Enum.GetValues(typeof(CommsMode));
            RefreshBridgePorts();
        }

        private void RefreshPortPairs()
        {
            ObservableCollection<Com0comPortPair> newpairs = Com0comSetup.GetPortPairs();

            //first we need to delete any ports that don't appear in the new list
            foreach (var expair in PortPairs.ToList())
            {
                var newpair = (from p in newpairs where p.PairNumber == expair.PairNumber select p).FirstOrDefault();
                if (newpair == null)
                {
                    expair.StopComms();
                    PortPairs.Remove(expair);
                }
            }

            //next we need to add any new pairs
            foreach (var newpair in newpairs)
            {
                var expair = (from p in PortPairs where p.PairNumber == newpair.PairNumber select p).FirstOrDefault();
                if (expair == null)
                {
                    PortPairs.Add(newpair);
                }
            }
        }

        private bool AllPortsCommsIdle()
        {
            foreach (var v in PortPairs)
            {
                if (v.CommsStatus == CommsStatus.Running)
                    return false;
            }
            return true;
        }

        #region UI Events

        private void mnuLaunchSetupg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Com0comSetup.LaunchSetupg();
            }
            catch (Win32Exception ex)
            {
                //e.g. the UAC prompt was cancelled
                MessageBox.Show("Unable to launch setupg: " + ex.Message);
            }
        }

        private void mnuExit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void mnuRemovePair_Click(object sender, RoutedEventArgs e)
        {
            Com0comPortPair p;
            if ((p = listPorts.SelectedValue as Com0comPortPair) != null)
            {
                if (p.CommsStatus != CommsStatus.Idle)
                {
                    MessageBox.Show("Please stop the comms on this port first");
                    return;
                }
                try
                {
                    if (Com0comSetup.DeletePortPair(p.PairNumber))
                    {
                        RefreshPortPairs();
                    }
                    else
                    {
                        MessageBox.Show("Failed to remove pair - do you have admin?");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception || ex is ApplicationException)
                {
                    MessageBox.Show("Failed to remove pair: " + ex.Message);
                }
            }
        }

        private void mnuRefreshPairs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshPortPairs();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is FormatException || ex is InvalidOperationException)
            {
                MessageBox.Show("Unable to list com0com port pairs: " + ex.Message);
            }
        }

        private void mnuAddPair_Click(object sender, RoutedEventArgs e)
        {
            PortConfigWindow w = new PortConfigWindow();
            if (w.ShowDialog() ?? false)
            {
                try
                {
                    if (Com0comSetup.CreatePortPair(w.Result.PortA))
                    {
                        RefreshPortPairs();
                    }
                    else
                    {
                        MessageBox.Show("Failed to create pair - do you have admin?");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception || ex is ApplicationException)
                {
                    MessageBox.Show("Failed to create pair: " + ex.Message);
                }
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            Com0comPortPair port = listPorts.SelectedValue as Com0comPortPair;
            if (port != null)
            {
                port.StopComms();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //stops only the hub4com started by this window
            Bridge.Disconnect();
            if (PortPairs == null)
                return;
            foreach (var pair in PortPairs)
            {
                pair.StopComms();
            }
        }

        private void btnPortSelect_Click(object sender, RoutedEventArgs e)
        {
            PortsDBSelect win = new PortsDBSelect();
            win.ShowDialog();
            if (win.Result != null)
            {
                Com0comPortPair p;
                if ((p = listPorts.SelectedValue as Com0comPortPair) != null)
                {
                    p.CommsMode = win.Result.Mode;
                    p.LocalPort = win.Result.LocalPort;
                    p.RemoteIP = win.Result.RemoteIP;
                    p.RemotePort = win.Result.RemotePort;
                }
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            Com0comPortPair port = listPorts.SelectedValue as Com0comPortPair;
            if (port != null)
            {
                port.StartComms();
            }
        }
        #endregion

        private void listPorts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            btnPortSelect_Click(sender, null);
        }

        #region COM Bridge

        private void RefreshBridgePorts()
        {
            string left = Bridge.LeftPort;
            string right = Bridge.RightPort;

            List<string> ports;
            string error;
            if (!ComPortNames.TryGetSorted(out ports, out error))
                Bridge.WriteLog(error);

            ComPorts.Clear();
            foreach (string port in ports)
                ComPorts.Add(port);
            //keep the selection when the port still exists
            Bridge.LeftPort = FindComPort(left);
            Bridge.RightPort = FindComPort(right);
            Bridge.WriteLog("COM ports: " + (ports.Count == 0 ? "(none)" : string.Join(", ", ports)));

            LoadBridgePresets();
        }

        private void LoadBridgePresets()
        {
            string path = Properties.Settings.Default.PortsDBLocation;
            List<BridgePreset> presets;
            try
            {
                presets = BridgePreset.Load(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Bridge.WriteLog("Unable to read COM Bridge presets from " + path + ": " + ex.Message);
                presets = new List<BridgePreset>();
            }

            BridgePresets.Clear();
            foreach (BridgePreset preset in presets)
                BridgePresets.Add(preset);
            Bridge.WriteLog(presets.Count + " COM Bridge preset(s) in " + path);
        }

        private string FindComPort(string name)
        {
            return ComPorts.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        }

        private void cboBridgePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BridgePreset preset = cboBridgePreset.SelectedItem as BridgePreset;
            if (preset == null)
                return;

            string left = FindComPort(preset.LeftPort);
            string right = FindComPort(preset.RightPort);
            if (left == null || right == null)
            {
                string missing = string.Join(", ", new[] { left == null ? preset.LeftPort : null, right == null ? preset.RightPort : null }.Where(p => p != null));
                Bridge.WriteLog("Preset " + preset.Name + ": " + missing + " not found");
                //do not leave the preset looking applied
                Dispatcher.BeginInvoke(new Action(() => cboBridgePreset.SelectedItem = null));
                MessageBox.Show(this, "Preset \"" + preset.Name + "\" uses " + missing + ", which was not found.\nClick Refresh after the port becomes available.", "COM Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Bridge.LeftPort = left;
            Bridge.RightPort = right;
            Bridge.WriteLog("Preset selected: " + preset);
        }

        private void btnBridgeRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshBridgePorts();
        }

        private async void btnBridgeConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Bridge.ConnectAsync();
            }
            catch (Exception ex)
            {
                //last resort: a failed connection must never take the application down
                Bridge.WriteLog("ERROR: " + ex);
                MessageBox.Show(this, ex.Message, "COM Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnBridgeDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Bridge.Disconnect();
        }

        private void Bridge_ErrorOccurred(object sender, string message)
        {
            MessageBox.Show(this, message, "COM Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void txtBridgeLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            txtBridgeLog.ScrollToEnd();
        }

        #endregion
    }
}
