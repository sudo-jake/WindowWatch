using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowWatch
{
    internal sealed class Preferences
    {
        internal const string DefaultKey = @"Software\WindowWatch";
        private readonly string key;
        private readonly Action<string> log;
        private bool reportedError;

        internal Preferences(string key, Action<string> log) { this.key = key; this.log = log; }

        private bool ReportStorageError(Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException || error is SecurityException)) return false;
            if (!reportedError) log("Could not access saved settings. " + error.Message);
            reportedError = true;
            return true;
        }
        internal object Read(string name)
        {
            try
            {
                using (var store = Registry.CurrentUser.OpenSubKey(key))
                    return store == null ? null : store.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch (Exception ex) { if (!ReportStorageError(ex)) throw; return null; }
        }
        internal void Save(string name, object value)
        {
            try
            {
                using (var store = Registry.CurrentUser.CreateSubKey(key)) store.SetValue(name, value);
            }
            catch (Exception ex) { if (!ReportStorageError(ex)) throw; }
        }
        internal void Bind(string name, CheckBox box)
        {
            object saved = Read(name);
            if (object.Equals(saved, 0) || object.Equals(saved, 1)) box.Checked = (int)saved == 1;
            box.CheckedChanged += delegate { Save(name, box.Checked ? 1 : 0); };
        }
        internal void Bind(string name, NumericUpDown field)
        {
            decimal number;
            if (decimal.TryParse(Read(name) as string, NumberStyles.Number, CultureInfo.InvariantCulture, out number)
                && number >= field.Minimum && number <= field.Maximum && decimal.Round(number, field.DecimalPlaces) == number)
                field.Value = number;
            field.ValueChanged += delegate { Save(name, field.Value.ToString(CultureInfo.InvariantCulture)); };
        }
    }

    internal sealed class WatchedWindow
    {
        internal readonly string ProcessName, Title;
        private readonly uint processId;
        private readonly long handle, processStart;
        private WatchedWindow(string process, string title, uint pid, long hwnd, long start)
        { ProcessName = process; Title = title; processId = pid; handle = hwnd; processStart = start; }

        private static long StartTime(uint pid)
        {
            try { using (var p = Process.GetProcessById((int)pid)) return p.StartTime.ToUniversalTime().Ticks; }
            catch (ArgumentException) { return 0; }
            catch (InvalidOperationException) { return 0; }
            catch (System.ComponentModel.Win32Exception) { return 0; }
        }
        internal static WatchedWindow From(Target target)
        { return new WatchedWindow(target.Process, target.Title, target.ProcessId, target.Handle.ToInt64(), StartTime(target.ProcessId)); }

        internal string[] Serialize()
        {
            var culture = CultureInfo.InvariantCulture;
            return new[] { ProcessName, Title, processId.ToString(culture), handle.ToString(culture), processStart.ToString(culture) };
        }
        internal static WatchedWindow Load(object value)
        {
            var parts = value as string[];
            if (parts == null || parts.Length != 5 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return null;
            uint pid; long hwnd, start;
            var culture = CultureInfo.InvariantCulture;
            if (!uint.TryParse(parts[2], NumberStyles.Integer, culture, out pid)
                || !long.TryParse(parts[3], NumberStyles.Integer, culture, out hwnd)
                || !long.TryParse(parts[4], NumberStyles.Integer, culture, out start)) return null;
            return new WatchedWindow(parts[0], parts[1], pid, hwnd, start);
        }
        internal Target Find(List<Target> targets)
        {
            if (processStart != 0)
            {
                var live = targets.Find(t => t.ProcessId == processId && t.Handle.ToInt64() == handle
                    && string.Equals(t.Process, ProcessName, StringComparison.OrdinalIgnoreCase));
                if (live != null && StartTime(processId) == processStart) return live;
            }
            var matches = targets.FindAll(t => string.Equals(t.Process, ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Title, Title, StringComparison.Ordinal));
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
