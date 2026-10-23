using System.Collections.Generic;
using System.Linq;

namespace Kuducopy.Core
{
    public enum PreviewAction { Copy, Replace, KeepBoth, Ask, Skip, Delete }

    public sealed class PreviewEntry
    {
        public PreviewAction Action { get; set; }
        public string Source { get; set; }
        public string Dest { get; set; }
        public long Size { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>What a job would do, worked out from robocopy's list-only run. Nothing is copied.</summary>
    public sealed class PreviewResult
    {
        public List<PreviewEntry> Entries { get; } = new List<PreviewEntry>();
        public JobSpec Spec { get; set; }

        public int Count(PreviewAction a) => Entries.Count(e => e.Action == a);
        public long Bytes(PreviewAction a) => Entries.Where(e => e.Action == a).Sum(e => e.Size);

        /// <summary>Bytes the job will write: new files, replacements and "keep both" copies (and files it asks about, at most).</summary>
        public long BytesToWrite => Entries.Where(e => e.Action == PreviewAction.Copy || e.Action == PreviewAction.Replace ||
                                                       e.Action == PreviewAction.KeepBoth || e.Action == PreviewAction.Ask).Sum(e => e.Size);

        /// <summary>
        /// Sorts the listed files by what will happen to them.
        /// Standard mode: the "If a file already exists" choice decides. Advanced mode: robocopy's options decide; every
        /// listed file is copied (replacing any file already there) and extra files are deleted when /MIR or /PURGE is on.
        /// </summary>
        public static PreviewResult Build(JobSpec spec, IEnumerable<PlannedFile> planned, IEnumerable<PlannedFile> extras)
        {
            var result = new PreviewResult { Spec = spec };
            foreach (var p in planned)
            {
                var e = new PreviewEntry { Source = p.Source, Dest = p.Dest, Size = p.Size, Note = Compare(p) };
                if (!p.Exists)
                {
                    e.Action = PreviewAction.Copy;
                    e.Note = "New";
                }
                else if (spec.Advanced)
                {
                    e.Action = PreviewAction.Replace;
                }
                else
                {
                    switch (spec.Conflict)
                    {
                        case Conflict.Overwrite: e.Action = PreviewAction.Replace; break;
                        case Conflict.ReplaceIfNewer: e.Action = p.CopiedBy(Conflict.ReplaceIfNewer) ? PreviewAction.Replace : PreviewAction.Skip; break;
                        case Conflict.Skip: e.Action = PreviewAction.Skip; break;
                        case Conflict.KeepBoth: e.Action = PreviewAction.KeepBoth; break;
                        default: e.Action = PreviewAction.Ask; break;
                    }
                }
                result.Entries.Add(e);
            }

            foreach (var x in extras ?? Enumerable.Empty<PlannedFile>())
                result.Entries.Add(new PreviewEntry { Action = PreviewAction.Delete, Dest = x.Dest, Size = x.Size, Note = "Only in the destination" });
            return result;
        }

        private static string Compare(PlannedFile p)
        {
            switch (p.Existing)
            {
                case Existing.Same: return "Already there, identical";
                case Existing.SourceNewer: return "Yours is newer";
                case Existing.SourceOlder: return "The one there is newer";
                case Existing.Different: return "Same date, different size";
                default: return "New";
            }
        }
    }
}
