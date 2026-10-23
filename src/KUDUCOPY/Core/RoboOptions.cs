using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace Kuducopy.Core
{
    public enum OptionKind
    {
        /// <summary>/E</summary>
        Flag,
        /// <summary>/MT:8</summary>
        Number,
        /// <summary>/COPY:DAT (letters from a fixed set)</summary>
        Letters,
        /// <summary>/LOG:C:\log.txt (one value joined with a colon)</summary>
        Text,
        /// <summary>/XF *.tmp *.bak (a list of separate arguments after the switch)</summary>
        List,
        /// <summary>/A+:RH (attribute letters)</summary>
        Attributes
    }

    /// <summary>One Robocopy option, as shown in Advanced mode.</summary>
    public sealed class OptionDef
    {
        public string Switch { get; set; }
        public string Group { get; set; }
        public OptionKind Kind { get; set; }
        public string Description { get; set; }
        public string Default { get; set; } = string.Empty;
        public string LetterSet { get; set; } = string.Empty;
        public string LetterHelp { get; set; } = string.Empty;
        public bool NeedsAdmin { get; set; }
        public bool Deletes { get; set; }

        /// <summary>A warning shown when the option is switched on.</summary>
        public string Warning { get; set; }

        /// <summary>Options that change how folders are walked; dropped for jobs that copy single files.</summary>
        public bool FolderOnly { get; set; }

        /// <summary>Runs forever or waits; never used for the quick list-only scan.</summary>
        public bool SkipInScan { get; set; }

        public string Label => Kind == OptionKind.Flag || Kind == OptionKind.List ? Switch : Switch + ":";
    }

    /// <summary>A chosen option and its value. Saved with jobs and presets.</summary>
    [DataContract]
    public sealed class SwitchValue
    {
        public SwitchValue() { }

        public SwitchValue(string sw, string value = "")
        {
            Switch = sw;
            Value = value ?? string.Empty;
        }

        [DataMember] public string Switch { get; set; }
        [DataMember] public string Value { get; set; } = string.Empty;
    }

    public static class RoboOptions
    {
        public const string GroupCopy = "Copy";
        public const string GroupSelect = "Files to include / skip";
        public const string GroupRetry = "Retry & errors";
        public const string GroupSpeed = "Speed & threads";
        public const string GroupLog = "Logging";
        public const string GroupMonitor = "Monitor & schedule";

        public static readonly string[] Groups = { GroupCopy, GroupSelect, GroupRetry, GroupSpeed, GroupLog, GroupMonitor };

        private const string FileLetters = "D data, A attributes, T timestamps, S security (permissions), O owner, U auditing, X skip alternate data streams";
        private const string DirLetters = "D data, A attributes, T timestamps, E extended attributes, X skip alternate data streams";
        private const string AttrLetters = "R read-only, A archive, S system, H hidden, C compressed, N not indexed, E encrypted, T temporary, O offline";

        private static OptionDef F(string g, string sw, string d, bool admin = false, bool deletes = false, string warn = null, bool folderOnly = false, bool skipScan = false) =>
            new OptionDef { Group = g, Switch = sw, Kind = OptionKind.Flag, Description = d, NeedsAdmin = admin, Deletes = deletes, Warning = warn, FolderOnly = folderOnly, SkipInScan = skipScan };

        private static OptionDef N(string g, string sw, string d, string def, bool folderOnly = false, bool skipScan = false, string warn = null) =>
            new OptionDef { Group = g, Switch = sw, Kind = OptionKind.Number, Description = d, Default = def, FolderOnly = folderOnly, SkipInScan = skipScan, Warning = warn };

        private static OptionDef T(string g, string sw, string d, string def = "", bool skipScan = false, string warn = null) =>
            new OptionDef { Group = g, Switch = sw, Kind = OptionKind.Text, Description = d, Default = def, SkipInScan = skipScan, Warning = warn };

        private static OptionDef L(string g, string sw, string d) =>
            new OptionDef { Group = g, Switch = sw, Kind = OptionKind.List, Description = d };

        private static OptionDef A(string g, string sw, string d) =>
            new OptionDef { Group = g, Switch = sw, Kind = OptionKind.Attributes, Description = d, LetterSet = "RASHCNETO", LetterHelp = AttrLetters };

        public static readonly IReadOnlyList<OptionDef> All = new List<OptionDef>
        {
            // ---------------- Copy
            F(GroupCopy, "/S", "Copy subfolders, but not empty ones", folderOnly: true),
            F(GroupCopy, "/E", "Copy subfolders, including empty ones", folderOnly: true),
            N(GroupCopy, "/LEV", "Only copy the top n levels of folders", "1", folderOnly: true),
            F(GroupCopy, "/Z", "Restartable mode: a broken copy continues where it stopped (slower for small files)"),
            F(GroupCopy, "/B", "Backup mode: copy files even without permission to read them", admin: true),
            F(GroupCopy, "/ZB", "Restartable mode, switching to backup mode when access is denied", admin: true),
            F(GroupCopy, "/J", "Unbuffered copy. Faster for very large files"),
            F(GroupCopy, "/EFSRAW", "Copy encrypted files in their encrypted form", admin: true),
            new OptionDef { Group = GroupCopy, Switch = "/COPY", Kind = OptionKind.Letters, Default = "DAT", LetterSet = "DATSOUX", LetterHelp = FileLetters,
                            Description = "What to copy for files" },
            new OptionDef { Group = GroupCopy, Switch = "/DCOPY", Kind = OptionKind.Letters, Default = "DA", LetterSet = "DATEX", LetterHelp = DirLetters,
                            Description = "What to copy for folders" },
            F(GroupCopy, "/SEC", "Copy files with security (same as /COPY:DATS)"),
            F(GroupCopy, "/COPYALL", "Copy all file information (same as /COPY:DATSOU)", admin: true),
            F(GroupCopy, "/NOCOPY", "Copy no file information (use with /PURGE)"),
            F(GroupCopy, "/SECFIX", "Fix security on all files, even skipped ones", admin: true),
            F(GroupCopy, "/TIMFIX", "Fix timestamps on all files, even skipped ones"),
            F(GroupCopy, "/NODCOPY", "Copy no folder information"),
            F(GroupCopy, "/NOOFFLOAD", "Copy files without Windows copy offload"),
            F(GroupCopy, "/COMPRESS", "Compress data while it travels over the network"),
            F(GroupCopy, "/SPARSE", "Keep sparse files sparse"),
            F(GroupCopy, "/NOCLONE", "Don't use block cloning (ReFS / Dev Drive)"),
            F(GroupCopy, "/SL", "Copy symbolic links as links, not the files they point to"),
            F(GroupCopy, "/SJ", "Copy junctions as junctions, not the folders they point to"),
            F(GroupCopy, "/CREATE", "Create the folder tree and empty (zero-length) files only"),
            F(GroupCopy, "/FAT", "Create destination files with 8.3 FAT names only"),
            F(GroupCopy, "/256", "Turn off support for very long paths (over 256 characters)"),
            A(GroupCopy, "/A+", "Add these attributes to copied files"),
            A(GroupCopy, "/A-", "Remove these attributes from copied files"),
            F(GroupCopy, "/PURGE", "Delete files and folders in the destination that aren't in the source", deletes: true,
                warn: "Purge DELETES files and folders in the destination that aren't in the source.", folderOnly: true),
            F(GroupCopy, "/MIR", "Mirror a folder: copy everything and DELETE extra files in the destination", deletes: true,
                warn: "Mirror DELETES files and folders in the destination that aren't in the source.", folderOnly: true),

            // ---------------- File selection
            L(GroupSelect, "/XF", "Skip files matching these names, paths or wildcards (e.g. *.tmp)"),
            L(GroupSelect, "/XD", "Skip folders matching these names or paths"),
            F(GroupSelect, "/A", "Only copy files with the Archive attribute set"),
            F(GroupSelect, "/M", "Only copy files with the Archive attribute, and reset it"),
            A(GroupSelect, "/IA", "Only include files with any of these attributes"),
            A(GroupSelect, "/XA", "Skip files with any of these attributes"),
            F(GroupSelect, "/XC", "Skip changed files (same time, different size)"),
            F(GroupSelect, "/XN", "Skip files that are newer in the source"),
            F(GroupSelect, "/XO", "Skip files that are older in the source"),
            F(GroupSelect, "/XX", "Skip extra files and folders (only in the destination). Keeps /PURGE from deleting them"),
            F(GroupSelect, "/XL", "Skip lonely files and folders (only in the source)"),
            F(GroupSelect, "/IM", "Include modified files (different change time)"),
            F(GroupSelect, "/IS", "Include same files (copy even if identical)"),
            F(GroupSelect, "/IT", "Include tweaked files (same size and time, different attributes)"),
            N(GroupSelect, "/MAX", "Skip files bigger than n bytes", "104857600"),
            N(GroupSelect, "/MIN", "Skip files smaller than n bytes", "1"),
            T(GroupSelect, "/MAXAGE", "Skip files older than n days (or a date as YYYYMMDD)", "30"),
            T(GroupSelect, "/MINAGE", "Skip files newer than n days (or a date as YYYYMMDD)", "1"),
            T(GroupSelect, "/MAXLAD", "Skip files not used since n days (or a date as YYYYMMDD)", "365"),
            T(GroupSelect, "/MINLAD", "Skip files used since n days (or a date as YYYYMMDD)", "1"),
            F(GroupSelect, "/XJ", "Skip junction points and symbolic links"),
            F(GroupSelect, "/XJD", "Skip symbolic links to folders and junctions"),
            F(GroupSelect, "/XJF", "Skip symbolic links to files"),
            F(GroupSelect, "/FFT", "Assume FAT file times (2-second precision). Useful for NAS and USB drives"),
            F(GroupSelect, "/DST", "Allow for 1-hour daylight saving time differences"),

            // ---------------- Retry
            N(GroupRetry, "/R", "Retries when a file can't be copied", "3"),
            N(GroupRetry, "/W", "Seconds to wait between retries", "5"),
            F(GroupRetry, "/REG", "Save the /R and /W values as Robocopy's defaults in the registry"),
            F(GroupRetry, "/TBD", "Wait for network share names to be defined (retry error 67)"),
            F(GroupRetry, "/LFSM", "Low free space mode: pause when the destination is nearly full"),

            // ---------------- Speed
            N(GroupSpeed, "/MT", "Copy with this many threads at once (1 to 128). Much faster for many small files", "8"),
            N(GroupSpeed, "/IPG", "Wait this many milliseconds between packets, to leave bandwidth for others", "50"),
            T(GroupSpeed, "/IoMaxSize", "Largest read/write size per copy cycle (e.g. 1m)", "1m"),
            T(GroupSpeed, "/IoRate", "Limit the copy speed (e.g. 50m = 50 MB per second)", "50m"),
            T(GroupSpeed, "/Threshold", "Only throttle files bigger than this (e.g. 100m)", "100m"),

            // ---------------- Logging
            T(GroupLog, "/LOG", "Write the log to this file (replaces it)", @"C:\robocopy.log"),
            T(GroupLog, "/LOG+", "Add the log to the end of this file", @"C:\robocopy.log"),
            T(GroupLog, "/UNILOG", "Write the log as Unicode to this file (replaces it)", @"C:\robocopy.log"),
            T(GroupLog, "/UNILOG+", "Add the Unicode log to the end of this file", @"C:\robocopy.log"),
            F(GroupLog, "/V", "Verbose: also list skipped files"),
            F(GroupLog, "/X", "Report all extra files, not just selected ones"),
            F(GroupLog, "/TS", "Show source file timestamps"),
            F(GroupLog, "/NS", "Don't log file sizes"),
            F(GroupLog, "/NFL", "Don't log file names", warn: "With /NFL KUDUCOPY can't show which file is being copied."),
            F(GroupLog, "/NP", "Don't show per-file progress", warn: "With /NP KUDUCOPY can only show progress when each file finishes."),
            F(GroupLog, "/ETA", "Show the estimated time of arrival for each file"),

            // ---------------- Monitor
            N(GroupMonitor, "/MON", "Keep watching the source and copy again after n changes", "1", skipScan: true,
                warn: "Monitoring keeps running until you press Cancel."),
            N(GroupMonitor, "/MOT", "Keep watching the source and copy again every n minutes if it changed", "5", skipScan: true,
                warn: "Monitoring keeps running until you press Cancel."),
            T(GroupMonitor, "/RH", "Only copy between these times of day (hhmm-hhmm, e.g. 2200-0600)", "2200-0600", skipScan: true),
            F(GroupMonitor, "/PF", "Check the run hours per file, not per pass", skipScan: true),
        };

        /// <summary>
        /// Added by KUDUCOPY so it can read the output. Not shown in Advanced mode, and neither are /MOV and /MOVE:
        /// the Move switch at the top of the window adds those.
        /// </summary>
        public static readonly string[] OutputSwitches = { "/BYTES", "/FP", "/NC", "/NDL", "/NJH", "/NJS", "/UNICODE" };

        private static readonly HashSet<string> Internal = new HashSet<string>(
            new[] { "/L", "/MOV", "/MOVE", "/BYTES", "/FP", "/NC", "/NDL", "/NJH", "/NJS", "/UNICODE", "/TEE", "/JOB", "/SAVE", "/QUIT", "/NOSD", "/NODD", "/IF", "/SD", "/DD" },
            StringComparer.OrdinalIgnoreCase);

        public static OptionDef Find(string sw) => All.FirstOrDefault(o => string.Equals(o.Switch, sw, StringComparison.OrdinalIgnoreCase));

        public static bool IsInternal(string sw) => Internal.Contains(sw);

        /// <summary>The arguments for one chosen option.</summary>
        public static List<string> ToArgs(SwitchValue v)
        {
            var def = Find(v.Switch);
            var value = (v.Value ?? string.Empty).Trim();
            if (def == null) return new List<string> { v.Switch };
            switch (def.Kind)
            {
                case OptionKind.Flag:
                    return new List<string> { def.Switch };
                case OptionKind.List:
                    var items = Args.Split(value);
                    if (items.Count == 0) return new List<string>();
                    items.Insert(0, def.Switch);
                    return items;
                default:
                    if (value.Length == 0) value = def.Default;
                    return new List<string> { def.Switch + ":" + value };
            }
        }

        /// <summary>Checks a value; returns a message, or null when it's fine.</summary>
        public static string Validate(SwitchValue v)
        {
            var def = Find(v.Switch);
            if (def == null) return null;
            var value = (v.Value ?? string.Empty).Trim();
            switch (def.Kind)
            {
                case OptionKind.Number:
                    if (value.Length == 0) return null;
                    if (!long.TryParse(value, out var n) || n < 0) return $"{def.Switch} needs a whole number.";
                    if (def.Switch == "/MT" && (n < 1 || n > 128)) return "/MT must be between 1 and 128 threads.";
                    return null;
                case OptionKind.Letters:
                case OptionKind.Attributes:
                    if (value.Length == 0) return def.Kind == OptionKind.Attributes ? $"{def.Switch} needs at least one attribute letter." : null;
                    var bad = value.ToUpperInvariant().Where(c => def.LetterSet.IndexOf(c) < 0).Distinct().ToList();
                    return bad.Count == 0 ? null : $"{def.Switch} doesn't know the letter(s) {string.Join(", ", bad)}. Use {def.LetterSet}.";
                case OptionKind.Text:
                    if (def.Switch == "/RH" && value.Length > 0 && !Regex.IsMatch(value, @"^\d{4}-\d{4}$"))
                        return "/RH needs two times like 2200-0600.";
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Which options this PC's robocopy.exe knows, read from "robocopy /?". Empty help means "assume all".</summary>
        public static HashSet<string> Supported(string helpText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in All)
            {
                if (string.IsNullOrEmpty(helpText) || Regex.IsMatch(helpText, Regex.Escape(o.Switch) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
                    set.Add(o.Switch);
            }
            return set;
        }

        /// <summary>The Robocopy version line from its help text, e.g. "10.0.22621.1".</summary>
        public static string Version(string helpText)
        {
            var m = Regex.Match(helpText ?? string.Empty, @"\b(\d{1,2}\.\d+\.\d{4,5}(?:\.\d+)?)\b");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        /// <summary>The options Advanced mode starts with: a sensible, safe copy.</summary>
        public static List<SwitchValue> Defaults() => new List<SwitchValue>
        {
            new SwitchValue("/E"),
            new SwitchValue("/COPY", "DAT"),
            new SwitchValue("/DCOPY", "T"),
            new SwitchValue("/R", "3"),
            new SwitchValue("/W", "5"),
        };
    }
}
