using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Com0com.Redirector
{
    /// <summary>
    /// A COM Bridge preset stored in portsdb.txt as "Name,COMBridge,LeftCOM,RightCOM".
    /// The existing redirector entries have five columns, so each reader ignores the other's lines.
    /// </summary>
    public class BridgePreset
    {
        public const string ModeName = "COMBridge";

        public BridgePreset(string name, string leftPort, string rightPort)
        {
            Name = name;
            LeftPort = leftPort;
            RightPort = rightPort;
        }

        public string Name { get; private set; }
        public string LeftPort { get; private set; }
        public string RightPort { get; private set; }

        public override string ToString()
        {
            return string.Format("{0} ({1} ⇔ {2})", Name, LeftPort, RightPort);
        }

        /// <summary>
        /// Read the COM Bridge presets from a ports database file. A missing file yields no presets.
        /// </summary>
        public static List<BridgePreset> Load(string path)
        {
            var presets = new List<BridgePreset>();
            if (!File.Exists(path))
                return presets;

            foreach (string line in File.ReadAllLines(path))
            {
                BridgePreset preset;
                if (TryParse(line, out preset))
                    presets.Add(preset);
            }
            return presets;
        }

        public static bool TryParse(string line, out BridgePreset preset)
        {
            preset = null;
            if (line == null)
                return false;

            string[] values = line.Split(',').Select(v => v.Trim()).ToArray();
            if (values.Length != 4 || !string.Equals(values[1], ModeName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (values[0].Length == 0 || !ComPortNames.IsSafeName(values[2]) || !ComPortNames.IsSafeName(values[3]))
                return false;

            preset = new BridgePreset(values[0], values[2], values[3]);
            return true;
        }
    }
}
