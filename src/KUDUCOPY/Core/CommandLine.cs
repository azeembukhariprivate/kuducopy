using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kuducopy.Core
{
    public enum Launch { Open, Paste, CopyTo, MoveTo, CopyToAsk, MoveToAsk, RegisterShell, Unregister, SelfTest, Job }

    /// <summary>
    /// KUDUCOPY.exe [files…]                          open with these as sources
    /// KUDUCOPY.exe --paste "D:\Folder"               paste what was copied/cut in File Explorer into this folder
    /// KUDUCOPY.exe --copy-to "D:\Backup" files…      copy straight to a folder (also --move-to)
    /// KUDUCOPY.exe --copy-to-ask files…              open the window with these files, to choose a folder (also --move-to-ask)
    /// KUDUCOPY.exe --register-shell | --unregister   used by the installer
    /// </summary>
    public sealed class CommandLine
    {
        public Launch Action { get; private set; } = Launch.Open;
        public string Folder { get; private set; }
        public List<string> Paths { get; } = new List<string>();

        public bool IsSetupOnly => Action == Launch.RegisterShell || Action == Launch.Unregister;

        /// <summary>Jobs from right-click menus that arrive close together are merged when this key matches.</summary>
        public string BatchKey => Action + "|" + (Folder ?? string.Empty).ToUpperInvariant();

        public static CommandLine Parse(IList<string> args)
        {
            var cmd = new CommandLine();
            args = args ?? new string[0];
            for (var i = 0; i < args.Count; i++)
            {
                var a = (args[i] ?? string.Empty).Trim();
                if (a.Length == 0) continue;
                switch (a.ToLowerInvariant())
                {
                    case "--paste": cmd.Action = Launch.Paste; cmd.Folder = Next(args, ref i); continue;
                    case "--copy-to": cmd.Action = Launch.CopyTo; cmd.Folder = Next(args, ref i); continue;
                    case "--move-to": cmd.Action = Launch.MoveTo; cmd.Folder = Next(args, ref i); continue;
                    case "--copy-to-ask": cmd.Action = Launch.CopyToAsk; continue;
                    case "--move-to-ask": cmd.Action = Launch.MoveToAsk; continue;
                    case "--register-shell": cmd.Action = Launch.RegisterShell; continue;
                    case "--unregister": cmd.Action = Launch.Unregister; continue;
                    case "--self-test": cmd.Action = Launch.SelfTest; continue;
                    case "--job": cmd.Action = Launch.Job; cmd.Folder = Next(args, ref i); continue; // a saved job, after restarting as administrator
                }
                if (a.StartsWith("--", StringComparison.Ordinal)) continue; // unknown option
                try { cmd.Paths.Add(Planner.Full(a)); }
                catch { /* ignore invalid paths */ }
            }
            if (cmd.Folder != null)
            {
                try { cmd.Folder = Planner.Full(cmd.Folder); }
                catch { cmd.Folder = null; }
            }
            if (cmd.Action == Launch.Job && (cmd.Folder == null || !Planner.IsSameOrInside(cmd.Folder, AppPaths.Temp)))
                cmd.Action = Launch.Open; // only job files KUDUCOPY wrote itself
            if ((cmd.Action == Launch.Paste || cmd.Action == Launch.CopyTo || cmd.Action == Launch.MoveTo) && string.IsNullOrEmpty(cmd.Folder))
                cmd.Action = Launch.Open;
            return cmd;
        }

        private static string Next(IList<string> args, ref int i) => i + 1 < args.Count ? args[++i] : null;

        public string[] ToArgs()
        {
            var list = new List<string>();
            switch (Action)
            {
                case Launch.Paste: list.Add("--paste"); list.Add(Folder); break;
                case Launch.CopyTo: list.Add("--copy-to"); list.Add(Folder); break;
                case Launch.MoveTo: list.Add("--move-to"); list.Add(Folder); break;
                case Launch.CopyToAsk: list.Add("--copy-to-ask"); break;
                case Launch.MoveToAsk: list.Add("--move-to-ask"); break;
            }
            list.AddRange(Paths);
            return list.ToArray();
        }

        public void Merge(CommandLine other)
        {
            foreach (var p in other.Paths)
                if (!Paths.Contains(p, StringComparer.OrdinalIgnoreCase)) Paths.Add(p);
        }
    }
}
