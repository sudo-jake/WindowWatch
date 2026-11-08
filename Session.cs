using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;

namespace WindowWatch
{
    internal sealed class Session
    {
        internal readonly string Folder, Window;
        private readonly DateTime started;
        internal volatile bool HasIndex;

        internal Session(string root, string title, string displayName)
        {
            started = DateTime.Now;
            Window = displayName;
            string name = started.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Folder = Path.Combine(root, WindowFolder(title), name);
        }
        internal static string WindowFolder(string title)
        {
            var name = new StringBuilder();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in title)
            {
                if (name.Length >= 56) break;
                name.Append(Array.IndexOf(invalid, c) < 0 ? c : '_');
            }
            if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1])) name.Length--;
            string safe = name.ToString().Trim().TrimEnd('.', ' ');
            if (safe.Length == 0) safe = "Window";
            if (Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³]) *(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                safe = "_" + safe;
            if (safe == title) return safe;
            using (var hash = SHA256.Create())
                return safe + "-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(title)), 0, 5).Replace("-", "");
        }
        private static string Row(string directory, DateTime localTime, string kind, int frames)
        {
            string href = Uri.EscapeDataString(Path.GetFileName(directory)) + "/index.html";
            return "<tr><td><a href=\"" + WebUtility.HtmlEncode(href) + "\">" + localTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "</a></td><td>" + WebUtility.HtmlEncode(kind) + "</td><td>" + frames + "</td></tr>";
        }
        internal void AddCapture(string directory, DateTime eventUtc, string kind, int frames)
        {
            UpdateIndex(Folder, delegate(string page)
            {
                if (page == null)
                {
                    using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("session.html"))) page = reader.ReadToEnd();
                    page = page.Replace("@@ROWS@@", "").Replace("@@START@@", started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                        .Replace("@@WINDOW@@", WebUtility.HtmlEncode(Window));
                }
                string row = Row(directory, eventUtc.ToLocalTime(), kind, frames);
                return Directory.Exists(directory) && !page.Contains(row) ? page.Replace("</tbody>", row + "</tbody>") : page;
            });
            HasIndex = true;
        }
        internal static void RefreshIndex(string folder)
        {
            UpdateIndex(folder, delegate(string page)
            {
                if (page == null) return null;
                int begin = page.IndexOf("<tbody>", StringComparison.Ordinal), end = page.IndexOf("</tbody>", StringComparison.Ordinal);
                if (begin < 0 || end < begin) throw new InvalidDataException("The session index is unreadable.");
                string rows = string.Concat(CaptureLibrary.InSession(folder).Select(c => Row(c.Folder, c.Time, c.Kind, c.Frames)));
                return page.Substring(0, begin + "<tbody>".Length) + rows + page.Substring(end);
            });
        }
        private static void UpdateIndex(string folder, Func<string,string> edit)
        {
            // Capture exports and library edits can also come from separate WindowWatch instances.
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant())));
            using (var mutex = new Mutex(false, "Local\\WindowWatch-index-" + key))
            {
                try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    string index = Path.Combine(folder,"index.html"), temp = index + ".tmp";
                    string previous = File.Exists(index) ? File.ReadAllText(index) : null;
                    if (previous != null && !previous.Contains("<title>WindowWatch session</title>")) throw new InvalidDataException("Unrecognized session index.");
                    string updated = edit(previous);
                    if (updated == previous) return;
                    File.WriteAllText(temp, updated);
                    if (previous == null) File.Move(temp, index); else File.Replace(temp, index, null);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
