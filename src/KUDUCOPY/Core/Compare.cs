using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kuducopy.Core
{
    public enum CompareState { LeftOnly, RightOnly, LeftNewer, RightNewer, Different, Same }

    public sealed class CompareEntry
    {
        /// <summary>Path below the two folders, like "March\photo.jpg".</summary>
        public string Relative { get; set; }
        public CompareState State { get; set; }
        public long LeftSize { get; set; } = -1;
        public long RightSize { get; set; } = -1;
        public DateTime LeftTime { get; set; }
        public DateTime RightTime { get; set; }

        /// <summary>Set when "compare contents" found the same data although the dates differ.</summary>
        public bool SameContent { get; set; }

        public bool OnLeft => LeftSize >= 0;
        public bool OnRight => RightSize >= 0;
        public long Size => Math.Max(LeftSize, RightSize);
    }

    /// <summary>Two folders side by side, file by file. Nothing is copied.</summary>
    public sealed class CompareResult
    {
        public string Left { get; set; }
        public string Right { get; set; }
        public bool Contents { get; set; }
        public List<CompareEntry> Entries { get; } = new List<CompareEntry>();

        /// <summary>Folders that couldn't be read (no access, gone while comparing).</summary>
        public List<string> Problems { get; } = new List<string>();

        public int Count(CompareState s) => Entries.Count(e => e.State == s);
        public bool Identical => Entries.All(e => e.State == CompareState.Same) && Problems.Count == 0;
    }

    public static class FolderCompare
    {
        /// <summary>Compares two folders and everything below them, like robocopy: same size and times within 2 seconds count as the same file.
        /// With <paramref name="contents"/> on, files of the same size are also read and compared byte for byte (by checksum).</summary>
        public static CompareResult Run(string left, string right, bool contents, Action<string> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            left = Planner.Full(left);
            right = Planner.Full(right);
            if (!Directory.Exists(left)) throw new InvalidOperationException($"\"{left}\" can't be found.");
            if (!Directory.Exists(right)) throw new InvalidOperationException($"\"{right}\" can't be found.");
            if (Planner.IsSameOrInside(left, right) || Planner.IsSameOrInside(right, left))
                throw new InvalidOperationException("Choose two separate folders: one of them is inside the other.");

            var result = new CompareResult { Left = left, Right = right, Contents = contents };
            var leftFiles = Walk(left, result.Problems, progress, cancel);
            var rightFiles = Walk(right, result.Problems, progress, cancel);

            foreach (var pair in leftFiles)
            {
                var e = new CompareEntry { Relative = pair.Key, LeftSize = pair.Value.Length, LeftTime = pair.Value.LastWriteTimeUtc };
                if (rightFiles.TryGetValue(pair.Key, out var r))
                {
                    e.RightSize = r.Length;
                    e.RightTime = r.LastWriteTimeUtc;
                    e.State = State(e);
                    if (contents && e.LeftSize == e.RightSize)
                    {
                        cancel.ThrowIfCancellationRequested();
                        progress?.Invoke(pair.Key);
                        try
                        {
                            var same = Checksums.HashFile(pair.Value.FullName, HashKind.Sha256, cancel: cancel) == Checksums.HashFile(r.FullName, HashKind.Sha256, cancel: cancel);
                            if (same && e.State != CompareState.Same) e.SameContent = true;
                            e.State = same ? CompareState.Same : e.State == CompareState.Same ? CompareState.Different : e.State;
                        }
                        catch (IOException ex) { result.Problems.Add(pair.Key + ": " + ex.Message); }
                        catch (UnauthorizedAccessException ex) { result.Problems.Add(pair.Key + ": " + ex.Message); }
                    }
                }
                else e.State = CompareState.LeftOnly;
                result.Entries.Add(e);
            }
            foreach (var pair in rightFiles.Where(p => !leftFiles.ContainsKey(p.Key)))
                result.Entries.Add(new CompareEntry { Relative = pair.Key, State = CompareState.RightOnly, RightSize = pair.Value.Length, RightTime = pair.Value.LastWriteTimeUtc });

            result.Entries.Sort((a, b) => a.State != b.State ? a.State.CompareTo(b.State) : string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static CompareState State(CompareEntry e)
        {
            switch (PlannedFile.Compare(e.LeftSize, e.LeftTime, e.RightSize, e.RightTime))
            {
                case Existing.Same: return CompareState.Same;
                case Existing.SourceNewer: return CompareState.LeftNewer;
                case Existing.SourceOlder: return CompareState.RightNewer;
                default: return CompareState.Different;
            }
        }

        /// <summary>Every file below a folder, keyed by its path relative to the folder. Linked folders (junctions) aren't followed.</summary>
        private static Dictionary<string, FileInfo> Walk(string root, List<string> problems, Action<string> progress, CancellationToken cancel)
        {
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root.Length : root.Length + 1;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                progress?.Invoke(dir.FullName);
                try
                {
                    foreach (var info in dir.EnumerateFileSystemInfos())
                    {
                        if (info is DirectoryInfo sub)
                        {
                            if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                        }
                        else if (info is FileInfo file)
                        {
                            files[file.FullName.Substring(prefix)] = file;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    problems.Add(dir.FullName + ": " + ex.Message);
                }
            }
            return files;
        }

        /// <summary>A job that copies these files from one side to the same place on the other side, replacing what's there.</summary>
        public static JobSpec CopySpec(CompareResult result, IEnumerable<CompareEntry> entries, bool toRight)
        {
            var from = toRight ? result.Left : result.Right;
            var to = toRight ? result.Right : result.Left;
            var tasks = entries
                .Where(e => toRight ? e.OnLeft : e.OnRight)
                .GroupBy(e => Path.GetDirectoryName(e.Relative) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RoboTask
                {
                    SourceDir = g.Key.Length == 0 ? from : Path.Combine(from, g.Key),
                    DestDir = g.Key.Length == 0 ? to : Path.Combine(to, g.Key),
                    Files = g.Select(e => Path.GetFileName(e.Relative)).ToList()
                })
                .ToList();
            return new JobSpec { Destination = to, Tasks = tasks, Conflict = Conflict.Overwrite };
        }

        /// <summary>Files that are copying the wrong way would replace a newer file.</summary>
        public static int ReplacesNewer(IEnumerable<CompareEntry> entries, bool toRight) =>
            entries.Count(e => toRight ? e.State == CompareState.RightNewer : e.State == CompareState.LeftNewer);

        public static string Describe(CompareState s)
        {
            switch (s)
            {
                case CompareState.LeftOnly: return "Only on the left";
                case CompareState.RightOnly: return "Only on the right";
                case CompareState.LeftNewer: return "Left is newer";
                case CompareState.RightNewer: return "Right is newer";
                case CompareState.Different: return "Different";
                default: return "Same";
            }
        }

        /// <summary>A plain text report, tab separated, for the clipboard or a file.</summary>
        public static string Report(CompareResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Left:\t" + result.Left);
            sb.AppendLine("Right:\t" + result.Right);
            sb.AppendLine("Compared:\t" + (result.Contents ? "size, date and contents" : "size and date"));
            sb.AppendLine();
            sb.AppendLine("State\tFile\tLeft size\tLeft modified\tRight size\tRight modified");
            foreach (var e in result.Entries)
                sb.AppendLine(string.Join("\t", Describe(e.State), e.Relative,
                    e.OnLeft ? e.LeftSize.ToString() : "", e.OnLeft ? e.LeftTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "",
                    e.OnRight ? e.RightSize.ToString() : "", e.OnRight ? e.RightTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : ""));
            foreach (var p in result.Problems) sb.AppendLine("Couldn't read\t" + p);
            return sb.ToString();
        }
    }
}
