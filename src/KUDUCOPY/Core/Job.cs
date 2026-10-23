using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace Kuducopy.Core
{
    public enum CopyMode { Copy, Move }

    public enum Conflict { Ask, Overwrite, ReplaceIfNewer, Skip, KeepBoth, Decide }

    public enum AfterFinish { Nothing, Close, Sound, Sleep, Shutdown }

    public enum HashKind { Md5, Sha1, Sha256, Crc32 }

    /// <summary>Everything about one copy or move job. Saved in History so it can be run again.</summary>
    [DataContract]
    public sealed class JobSpec
    {
        [DataMember] public List<string> Sources { get; set; } = new List<string>();
        [DataMember] public string Destination { get; set; } = string.Empty;
        [DataMember] public CopyMode Mode { get; set; }
        [DataMember] public Conflict Conflict { get; set; } = Conflict.Ask;
        [DataMember] public bool Verify { get; set; }
        [DataMember] public HashKind Hash { get; set; } = HashKind.Md5;
        [DataMember] public bool KeepGoing { get; set; } = true;
        [DataMember] public AfterFinish After { get; set; }
        [DataMember] public bool Advanced { get; set; }
        [DataMember] public List<SwitchValue> Switches { get; set; } = new List<SwitchValue>();
        [DataMember] public string ExtraOptions { get; set; } = string.Empty;

        /// <summary>Set for "Retry failed files": copy exactly these files instead of Sources.</summary>
        [DataMember] public List<RoboTask> Tasks { get; set; }

        public JobSpec Clone()
        {
            var c = (JobSpec)MemberwiseClone();
            c.Sources = new List<string>(Sources ?? new List<string>());
            c.Switches = (Switches ?? new List<SwitchValue>()).Select(s => new SwitchValue(s.Switch, s.Value)).ToList();
            c.Tasks = Tasks?.Select(t => t.Clone()).ToList();
            return c;
        }

        public string Verb => Mode == CopyMode.Move ? "Move" : "Copy";
        public string Verbing => Mode == CopyMode.Move ? "Moving" : "Copying";

        public string SourceSummary
        {
            get
            {
                var names = (Tasks != null && Tasks.Count > 0
                        ? Tasks.SelectMany(t => t.Files.Count > 0 ? t.Files : new List<string> { Path.GetFileName(t.SourceDir.TrimEnd('\\')) })
                        : Sources.Select(Planner.DisplayName))
                    .ToList();
                if (names.Count == 0) return "nothing";
                if (names.Count == 1) return names[0];
                if (names.Count == 2) return names[0] + " and " + names[1];
                return $"{names[0]}, {names[1]} and {names.Count - 2} more";
            }
        }
    }

    /// <summary>One robocopy.exe run: a source folder, a destination folder and optionally a list of file names.</summary>
    [DataContract]
    public sealed class RoboTask
    {
        [DataMember] public string SourceDir { get; set; }
        [DataMember] public string DestDir { get; set; }

        /// <summary>Empty: the whole folder. Otherwise only these file names from SourceDir.</summary>
        [DataMember] public List<string> Files { get; set; } = new List<string>();

        public bool IsFolder => Files == null || Files.Count == 0;

        public RoboTask Clone() => new RoboTask { SourceDir = SourceDir, DestDir = DestDir, Files = new List<string>(Files ?? new List<string>()) };
    }

    /// <summary>Turns the user's choices into robocopy runs and arguments.</summary>
    public static class Planner
    {
        public static string DisplayName(string path)
        {
            var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
            var name = trimmed.Split('\\', '/').Last();
            if (name.Length == 2 && name[1] == ':') return "Drive " + char.ToUpperInvariant(name[0]);
            return name.Length == 0 ? trimmed : name;
        }

        /// <summary>Groups the sources into robocopy runs. Throws with a friendly message when something can't work.</summary>
        public static List<RoboTask> Tasks(JobSpec spec)
        {
            if (spec.Tasks != null && spec.Tasks.Count > 0) return spec.Tasks.Select(t => t.Clone()).ToList();
            if (spec.Sources == null || spec.Sources.Count == 0) throw new InvalidOperationException("Add some files or folders to copy first.");
            if (string.IsNullOrWhiteSpace(spec.Destination)) throw new InvalidOperationException("Choose where to copy them to.");

            var dest = Full(spec.Destination);
            var tasks = new List<RoboTask>();
            var fileGroups = new Dictionary<string, RoboTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in spec.Sources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = Full(raw);
                if (Directory.Exists(source))
                {
                    var target = Path.Combine(dest, DisplayName(source));
                    if (IsSameOrInside(dest, source))
                        throw new InvalidOperationException($"Can't copy \"{DisplayName(source)}\" into itself. Choose a destination outside that folder.");
                    tasks.Add(new RoboTask { SourceDir = source, DestDir = target });
                }
                else if (File.Exists(source))
                {
                    var parent = Path.GetDirectoryName(source);
                    if (string.Equals(parent?.TrimEnd('\\'), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"\"{Path.GetFileName(source)}\" is already in {dest}. Choose a different destination.");
                    if (!fileGroups.TryGetValue(parent, out var task))
                    {
                        task = new RoboTask { SourceDir = parent, DestDir = dest };
                        fileGroups[parent] = task;
                        tasks.Add(task);
                    }
                    task.Files.Add(Path.GetFileName(source));
                }
                else
                {
                    throw new InvalidOperationException($"\"{raw}\" can't be found. It may have been moved or deleted.");
                }
            }
            return tasks;
        }

        public static string Full(string path)
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }

        public static bool IsSameOrInside(string path, string folder)
        {
            var sep = Path.DirectorySeparatorChar;
            var p = Full(path).TrimEnd(sep) + sep;
            var f = Full(folder).TrimEnd(sep) + sep;
            return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Robocopy wants folders without a trailing backslash, except drive roots ("D:\") which it handles itself.</summary>
        private static string Dir(string d) => d.Length > 3 ? d.TrimEnd('\\') : d;

        /// <summary>
        /// The arguments for a real copy (scan = false) or the quick list-only scan (scan = true).
        /// copyPolicy: the rule robocopy applies to files that already exist.
        /// </summary>
        public static List<string> Build(JobSpec spec, RoboTask task, bool scan, Conflict copyPolicy, bool moveWithRobocopy, IEnumerable<string> excludeFiles = null)
        {
            var a = new List<string> { Dir(task.SourceDir), Dir(task.DestDir) };
            a.AddRange(task.Files);

            if (spec.Advanced)
            {
                foreach (var sv in spec.Switches ?? new List<SwitchValue>())
                {
                    var def = RoboOptions.Find(sv.Switch);
                    if (RoboOptions.IsInternal(sv.Switch)) continue;
                    if (RoboOptions.IsLogOption(sv.Switch)) continue; // KUDUCOPY writes the user's log file itself (see CopyJob)
                    if (def != null && def.FolderOnly && !task.IsFolder) continue; // never walk or purge folders for single files
                    if (def != null && def.SkipInScan && scan) continue;
                    a.AddRange(RoboOptions.ToArgs(sv));
                }
                foreach (var extra in Args.Split(spec.ExtraOptions))
                {
                    if (Regex(extra, "^/(LOG|UNILOG|TEE|UNICODE)")) continue;
                    if (scan && Regex(extra, "^/(MON|MOT|RH)")) continue;
                    a.Add(extra);
                }
            }
            else
            {
                if (task.IsFolder) a.Add("/E");
                a.Add("/COPY:DAT");
                a.Add("/DCOPY:T");
                if (scan)
                {
                    // List everything, so KUDUCOPY can see which files already exist.
                    a.Add("/IS");
                    a.Add("/IT");
                }
                else
                {
                    a.AddRange(PolicyArgs(copyPolicy));
                }
                a.Add(spec.KeepGoing ? "/R:3" : "/R:0");
                a.Add(spec.KeepGoing ? "/W:5" : "/W:0");
            }

            if (!scan && moveWithRobocopy) a.Add(task.IsFolder ? "/MOVE" : "/MOV");

            if (excludeFiles != null)
            {
                var list = excludeFiles.ToList();
                if (list.Count > 0)
                {
                    a.Add("/XF");
                    a.AddRange(list);
                }
            }

            if (scan) a.Add("/L");
            a.AddRange(RoboOptions.OutputSwitches);
            return a;
        }

        public static IEnumerable<string> PolicyArgs(Conflict policy)
        {
            switch (policy)
            {
                case Conflict.Overwrite: return new[] { "/IS", "/IT" };
                case Conflict.ReplaceIfNewer: return new[] { "/XO" };
                case Conflict.Skip:
                case Conflict.KeepBoth:
                case Conflict.Decide:
                case Conflict.Ask:
                    return new[] { "/XC", "/XN", "/XO" };
                default: return new string[0];
            }
        }

        private static bool Regex(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The robocopy command shown in the preview and copied to the clipboard.</summary>
        public static string Preview(JobSpec spec, RoboTask task) =>
            "robocopy " + Args.Join(Build(spec, task, false, spec.Conflict == Conflict.Ask ? Conflict.Skip : spec.Conflict,
                spec.Mode == CopyMode.Move && !spec.Verify));

        /// <summary>The volume a path lives on, used to queue jobs on the same drive one after another.</summary>
        public static string VolumeKey(string path)
        {
            var p = (path ?? string.Empty).Trim().Trim('"');
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = p.TrimStart('\\').Split('\\');
                return (@"\\" + string.Join(@"\", parts.Take(2))).ToUpperInvariant();
            }
            if (p.Length >= 2 && p[1] == ':') return char.ToUpperInvariant(p[0]) + @":\";
            try { return (Path.GetPathRoot(Full(p)) ?? p).ToUpperInvariant(); }
            catch { return p.ToUpperInvariant(); }
        }
    }
}
