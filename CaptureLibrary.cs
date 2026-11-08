using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace WindowWatch
{
    internal sealed class SavedCapture
    {
        internal string Folder, SessionFolder, Window, Kind;
        internal DateTime Time;
        internal int Frames;
    }

    internal static class CaptureLibrary
    {
        private static readonly Regex ClipName = new Regex(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3})_(Large-redraw|Brief-flash|Pause-during-motion|Stopped-watching)_[a-fA-F0-9]{6}$");
        private static readonly Regex SessionName = new Regex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(-\d{3}_[a-fA-F0-9]{12})?$");
        private static readonly Regex FrameName = new Regex(@"^[0-9]{4,}\.png$", RegexOptions.IgnoreCase);
        private static readonly string[] Metadata = { "index.html", "timeline.csv", "events.csv", "samples.csv" };

        private static bool Plain(DirectoryInfo dir)
        {
            return dir != null && dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        private static DirectoryInfo[] Children(DirectoryInfo folder)
        {
            try { return folder.GetDirectories().Where(Plain).ToArray(); }
            catch (UnauthorizedAccessException) { return new DirectoryInfo[0]; }
            catch (DirectoryNotFoundException) { return new DirectoryInfo[0]; }
        }
        private static SavedCapture Read(DirectoryInfo folder, DirectoryInfo session)
        {
            var match = ClipName.Match(folder.Name);
            DateTime time;
            if (!match.Success || !Plain(folder) || !DateTime.TryParseExact(match.Groups[1].Value,
                "yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) return null;
            try
            {
                if (!Metadata.All(name => File.Exists(Path.Combine(folder.FullName, name))) || !File.Exists(Path.Combine(folder.FullName, "0001.png"))) return null;
                return new SavedCapture { Folder = folder.FullName, SessionFolder = session == null ? null : session.FullName,
                    Window = session == null ? LegacyWindow(folder.FullName) : session.Parent.Name, Time = time,
                    Kind = match.Groups[2].Value.Replace('-', ' '), Frames = folder.GetFiles("*.png").Count(f => FrameName.IsMatch(f.Name)) };
            }
            catch (DirectoryNotFoundException) { return null; }
            catch (FileNotFoundException) { return null; }
        }
        private static string LegacyWindow(string folder)
        {
            using (var reader = File.OpenText(Path.Combine(folder,"index.html")))
            {
                var header = new char[8192];
                var match = Regex.Match(new string(header,0,reader.ReadBlock(header,0,header.Length)), "<h1>(.*?)</h1>");
                return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : "Earlier capture";
            }
        }
        internal static List<SavedCapture> InSession(string folder)
        {
            var dir = new DirectoryInfo(folder);
            return dir.GetDirectories().Where(Plain).Select(d => Read(d, dir)).Where(c => c != null).OrderBy(c => c.Time).ToList();
        }
        internal static List<SavedCapture> Scan(string root)
        {
            var library = new DirectoryInfo(root);
            var clips = new List<SavedCapture>();
            if (!library.Exists) return clips;
            foreach (var dir in Children(library))
            {
                if (ClipName.IsMatch(dir.Name))
                {
                    var legacy = Read(dir, null);
                    if (legacy != null) clips.Add(legacy);
                }
                else
                    foreach (var session in Children(dir).Where(s => SessionName.IsMatch(s.Name)))
                        clips.AddRange(InSession(session.FullName));
            }
            return clips.OrderByDescending(c => c.Time).ToList();
        }

        internal static void Recycle(string root, SavedCapture selected)
        {
            var library = new DirectoryInfo(root);
            var folder = new DirectoryInfo(selected.Folder);
            var parent = folder.Parent;
            if (!Plain(library) || !Plain(folder) || !Plain(parent)) throw new IOException("The capture location changed. Refresh the list.");
            bool legacy = string.Equals(parent.FullName, library.FullName, StringComparison.OrdinalIgnoreCase);
            if (!legacy && (!SessionName.IsMatch(parent.Name) || !Plain(parent.Parent) || parent.Parent.Parent == null ||
                !string.Equals(parent.Parent.Parent.FullName, library.FullName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This folder is outside the selected capture library.");
            if (Read(folder, legacy ? null : parent) == null) throw new IOException("The folder is no longer a complete WindowWatch capture.");
            foreach (var file in folder.GetFileSystemInfos())
                if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    (!FrameName.IsMatch(file.Name) && !Metadata.Contains(file.Name, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException("The capture contains additional files. Review it in Explorer.");
            RecycleBin.Move(folder.FullName);
        }
    }
}
