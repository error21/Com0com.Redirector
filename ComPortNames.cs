using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;

namespace Com0com.Redirector
{
    /// <summary>
    /// COM port names as reported by Windows, naturally sorted (COM2 before COM10).
    /// </summary>
    public static class ComPortNames
    {
        //only plain names such as COM10 or CNCB0 may be passed to hub4com
        private static readonly Regex SafeName = new Regex(@"^[A-Za-z][A-Za-z0-9]{0,31}$");
        private static readonly Regex NumberedName = new Regex(@"^(?<prefix>[A-Za-z]*?)(?<number>\d+)$");

        /// <summary>
        /// Get the COM ports that currently exist in Windows, naturally sorted.
        /// Names that are not plain alphanumeric are dropped.
        /// </summary>
        public static List<string> GetSorted()
        {
            return SerialPort.GetPortNames()
                .Where(IsSafeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, Comparer<string>.Create(CompareNatural))
                .ToList();
        }

        /// <summary>
        /// Same as GetSorted, but reports a failure to query Windows as an error message.
        /// </summary>
        public static bool TryGetSorted(out List<string> ports, out string error)
        {
            try
            {
                ports = GetSorted();
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is InvalidCastException)
            {
                ports = new List<string>();
                error = "Unable to list the COM ports: " + ex.Message;
                return false;
            }
        }

        public static bool IsSafeName(string name)
        {
            return name != null && SafeName.IsMatch(name);
        }

        /// <summary>
        /// Compare port names by prefix, then by the numeric suffix as a number.
        /// </summary>
        public static int CompareNatural(string x, string y)
        {
            Match mx = NumberedName.Match(x ?? "");
            Match my = NumberedName.Match(y ?? "");
            if (mx.Success && my.Success)
            {
                int result = string.Compare(mx.Groups["prefix"].Value, my.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase);
                if (result != 0)
                    return result;

                string nx = mx.Groups["number"].Value.TrimStart('0');
                string ny = my.Groups["number"].Value.TrimStart('0');
                result = nx.Length.CompareTo(ny.Length);
                if (result != 0)
                    return result;
                result = string.CompareOrdinal(nx, ny);
                if (result != 0)
                    return result;
            }
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
