using System;
using System.Collections.Generic;
using System.Linq;

namespace Kuducopy.Core
{
    /// <summary>Reads Robocopy job files (.rcj, made with robocopy /SAVE:name).</summary>
    public sealed class JobFile
    {
        public string Source { get; set; }
        public string Destination { get; set; }
        public List<SwitchValue> Switches { get; } = new List<SwitchValue>();

        /// <summary>Options KUDUCOPY doesn't list; kept as "Extra options".</summary>
        public List<string> Unknown { get; } = new List<string>();

        public static JobFile Parse(IEnumerable<string> lines)
        {
            var job = new JobFile();
            SwitchValue list = null;
            var skipItems = false;
            foreach (var raw in lines)
            {
                var line = raw;
                var comment = line.IndexOf("::", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;

                if (!line.StartsWith("/", StringComparison.Ordinal))
                {
                    // An item under /XF, /XD or /IF.
                    if (list != null) list.Value = (list.Value + " " + Args.Quote(line)).Trim();
                    else if (!skipItems) job.Unknown.Add(line);
                    continue;
                }

                list = null;
                skipItems = false;
                var colon = line.IndexOf(':');
                var name = colon > 0 ? line.Substring(0, colon) : line;
                var value = colon > 0 ? line.Substring(colon + 1) : string.Empty;

                if (name.Equals("/SD", StringComparison.OrdinalIgnoreCase)) { job.Source = Trim(value); continue; }
                if (name.Equals("/DD", StringComparison.OrdinalIgnoreCase)) { job.Destination = Trim(value); continue; }
                if (name.Equals("/IF", StringComparison.OrdinalIgnoreCase)) { skipItems = true; continue; } // file filters: KUDUCOPY copies whole folders
                if (RoboOptions.IsInternal(name)) continue;

                var def = RoboOptions.Find(name);
                if (def == null)
                {
                    job.Unknown.Add(line);
                    continue;
                }
                var sv = new SwitchValue(def.Switch, def.Kind == OptionKind.Flag ? string.Empty : value);
                job.Switches.RemoveAll(s => s.Switch == def.Switch);
                job.Switches.Add(sv);
                if (def.Kind == OptionKind.List) list = sv;
            }
            return job;
        }

        private static string Trim(string path)
        {
            var p = path.Trim().Trim('"');
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }
    }
}
