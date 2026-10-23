using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kuducopy.Core
{
    public enum LineKind { Other, File, Percent, Error }

    public sealed class RoboLine
    {
        public LineKind Kind { get; set; }
        public long Size { get; set; }
        public string Path { get; set; }
        public double Percent { get; set; }
        public int ErrorCode { get; set; }
    }

    /// <summary>
    /// Reads robocopy's output when it runs with /BYTES /FP /NC /NDL /NJH /NJS.
    /// Works without relying on English words, so it also works on Windows in other languages.
    /// </summary>
    public static class RoboOutput
    {
        // "        123456	C:\Folder\file.txt"  (size, then the full path)
        private static readonly Regex FileLine = new Regex(@"^\s*(?<size>\d+)\s+(?<path>(?:[A-Za-z]:\\|\\\\).+?)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex PercentLine = new Regex(@"^\s*(?<p>\d{1,3}(?:[.,]\d+)?)%\s*$", RegexOptions.CultureInvariant);
        // "2026/10/22 14:12:03 ERROR 32 (0x00000020) Copying File C:\Folder\Thumbs.db"
        private static readonly Regex ErrorLine = new Regex(@"\s(?<code>\d+)\s+\(0x(?<hex>[0-9A-Fa-f]{8})\)\s*(?<rest>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex PathStart = new Regex(@"(?:[A-Za-z]:\\|\\\\)", RegexOptions.CultureInvariant);

        public static RoboLine Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return new RoboLine();

            var m = PercentLine.Match(line);
            if (m.Success)
            {
                double.TryParse(m.Groups["p"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p);
                return new RoboLine { Kind = LineKind.Percent, Percent = Math.Max(0, Math.Min(100, p)) };
            }

            m = ErrorLine.Match(line);
            if (m.Success)
            {
                var rest = m.Groups["rest"].Value;
                var ps = PathStart.Match(rest);
                return new RoboLine
                {
                    Kind = LineKind.Error,
                    ErrorCode = int.TryParse(m.Groups["code"].Value, out var code) ? code : 0,
                    Path = ps.Success ? rest.Substring(ps.Index).Trim() : string.Empty
                };
            }

            m = FileLine.Match(line);
            if (m.Success && long.TryParse(m.Groups["size"].Value, out var size))
                return new RoboLine { Kind = LineKind.File, Size = size, Path = m.Groups["path"].Value };

            return new RoboLine();
        }

        /// <summary>A plain-language reason for a Windows error code, in the user's language.</summary>
        public static string Reason(int code)
        {
            switch (code)
            {
                case 0: return "Not copied";
                case 5: return "Access is denied";
                case 32: return "In use by another program";
                case 33: return "Part of the file is locked by another program";
                case 112: return "Not enough space on the destination";
                case 123: return "The name isn't valid on the destination";
                case 206: return "The name or path is too long";
                case 1392: return "The file or folder is damaged and can't be read";
            }
            try { return new Win32Exception(code).Message.TrimEnd('.', '\r', '\n', ' '); }
            catch { return "Error " + code; }
        }

        /// <summary>Exit codes 8 and up mean some files failed; 16 means robocopy couldn't run at all.</summary>
        public static bool IsFatal(int exitCode) => exitCode >= 16 || exitCode < 0;
    }

    public enum Existing { None, Same, SourceNewer, SourceOlder, Different }

    /// <summary>One file the job will (or might) copy.</summary>
    public sealed class PlannedFile
    {
        public string Source { get; set; }
        public string Dest { get; set; }
        public long Size { get; set; }
        public DateTime SourceTime { get; set; }
        public Existing Existing { get; set; }
        public long DestSize { get; set; }
        public DateTime DestTime { get; set; }
        public RoboTask Task { get; set; }

        public bool Exists => Existing != Existing.None;

        /// <summary>Whether robocopy's rule for this policy copies the file.</summary>
        public bool CopiedBy(Conflict policy)
        {
            switch (policy)
            {
                case Conflict.Overwrite: return true;
                case Conflict.ReplaceIfNewer: return !Exists || Existing == Existing.SourceNewer || Existing == Existing.Different;
                default: return !Exists;
            }
        }

        /// <summary>Compares two files like robocopy: same size, and times within 2 seconds.</summary>
        public static Existing Compare(long sourceSize, DateTime sourceTime, long destSize, DateTime destTime)
        {
            var diff = (sourceTime - destTime).TotalSeconds;
            if (Math.Abs(diff) <= 2) return sourceSize == destSize ? Existing.Same : Existing.Different;
            return diff > 0 ? Existing.SourceNewer : Existing.SourceOlder;
        }

        /// <summary>A free name for "keep both": "photo (2).jpg", "photo (3).jpg", …</summary>
        public static string KeepBothName(string dest, Func<string, bool> exists)
        {
            var dir = System.IO.Path.GetDirectoryName(dest) ?? string.Empty;
            var name = System.IO.Path.GetFileNameWithoutExtension(dest);
            var ext = System.IO.Path.GetExtension(dest);
            for (var i = 2; i < 10000; i++)
            {
                var candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
                if (!exists(candidate)) return candidate;
            }
            return System.IO.Path.Combine(dir, $"{name} ({Guid.NewGuid().ToString("N").Substring(0, 6)}){ext}");
        }
    }
}
