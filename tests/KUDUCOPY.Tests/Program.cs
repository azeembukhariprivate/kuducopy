using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kuducopy.Core;

namespace Kuducopy.Tests
{
    /// <summary>A tiny test runner: each check prints PASS or FAIL, and the exit code is the number of failures.</summary>
    internal static class Program
    {
        private static int _failures;
        private static int _passes;

        private static int Main()
        {
            Run("Reads robocopy output", ReadsOutput);
            Run("Builds standard commands", BuildsStandardCommands);
            Run("Builds advanced commands", BuildsAdvancedCommands);
            Run("Groups sources into robocopy runs", PlansTasks);
            Run("Option catalogue and validation", OptionCatalogue);
            Run("Detects supported options", DetectsSupportedOptions);
            Run("Existing-file rules", ConflictRules);
            Run("Keep-both names", KeepBothNames);
            Run("Checksums", ChecksumValues);
            Run("Checksum files", ChecksumFiles);
            Run("Quotes and splits arguments", QuotesArguments);
            Run("Command line", ParsesCommandLine);
            Run("Right-click menu entries", ShellEntries);
            Run("History and retry", HistoryAndRetry);
            Run("Formats sizes", FormatsSizes);
            Run("Reads job files", ReadsJobFiles);
            Run("Preview sorts files by what will happen", PreviewSorts);
            Run("Mirror", MirrorRules);
            Run("Compare two folders", CompareFolders);

            Console.WriteLine();
            Console.WriteLine($"{_passes} passed, {_failures} failed");
            return _failures;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passes++;
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("FAIL  " + name + "\n      " + ex.Message);
            }
        }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected <{expected}> but got <{actual}>");
        }

        private static readonly RoboTask Folder = new RoboTask { SourceDir = @"C:\Users\Azeem\Pictures\Photos 2026", DestDir = @"D:\Backup\Photos 2026" };
        private static readonly RoboTask Files = new RoboTask { SourceDir = @"C:\Docs", DestDir = @"D:\Backup", Files = new List<string> { "a.pdf", "my file.txt" } };

        // ---------------------------------------------------------------------------------------

        private static void ReadsOutput()
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "robocopy-output.txt"), Encoding.UTF8)
                .Select(RoboOutput.Parse).ToList();
            var files = lines.Where(l => l.Kind == LineKind.File).ToList();
            Equal(4, files.Count, "file lines");
            Equal(6291456L, files[0].Size, "size");
            Equal(@"C:\Users\Azeem\Pictures\Photos 2026\IMG_4021.jpg", files[0].Path, "path with spaces");
            Equal(1468006400L, files[1].Size, "big size");
            Equal(@"C:\Users\Azeem\Pictures\Photos 2026\Ünïcødé ファイル.txt", files[3].Path, "unicode path");

            var pct = lines.Where(l => l.Kind == LineKind.Percent).Select(l => l.Percent).ToList();
            Equal("0,45.5,100,0,62,100", string.Join(",", pct), "percent lines");

            var errors = lines.Where(l => l.Kind == LineKind.Error).ToList();
            Equal(2, errors.Count, "error lines (English and German)");
            Equal(32, errors[0].ErrorCode, "error code");
            Equal(@"C:\Users\Azeem\Pictures\Photos 2026\Thumbs.db", errors[0].Path, "error path");
            Equal(5, errors[1].ErrorCode, "localised error code");
            Equal(@"C:\Users\Azeem\Pictures\Photos 2026\desktop.ini", errors[1].Path, "localised error path");

            Equal(LineKind.Other, RoboOutput.Parse("Waiting 5 seconds... Retrying...").Kind, "other text");
            Equal(LineKind.Other, RoboOutput.Parse("   Total    Copied   Skipped").Kind, "summary header");
            Equal("In use by another program", RoboOutput.Reason(32), "reason 32");
            Equal("Access is denied", RoboOutput.Reason(5), "reason 5");
            Check(RoboOutput.IsFatal(16) && !RoboOutput.IsFatal(8) && !RoboOutput.IsFatal(1), "exit codes");
        }

        private static string Cmd(JobSpec spec, RoboTask task, bool scan, Conflict policy, bool move = false, IEnumerable<string> exclude = null) =>
            Args.Join(Planner.Build(spec, task, scan, policy, move, exclude));

        private const string Out = "/BYTES /FP /NC /NDL /NJH /NJS";

        private static void BuildsStandardCommands()
        {
            var spec = new JobSpec { Destination = @"D:\Backup" };
            Equal($"\"C:\\Users\\Azeem\\Pictures\\Photos 2026\" \"D:\\Backup\\Photos 2026\" /E /COPY:DAT /DCOPY:T /XC /XN /XO /R:3 /W:5 {Out}",
                Cmd(spec, Folder, false, Conflict.Skip), "skip existing");
            Equal($"\"C:\\Users\\Azeem\\Pictures\\Photos 2026\" \"D:\\Backup\\Photos 2026\" /E /COPY:DAT /DCOPY:T /IS /IT /R:3 /W:5 /L {Out}",
                Cmd(spec, Folder, true, Conflict.Skip), "scan lists everything");
            Equal($"C:\\Docs D:\\Backup a.pdf \"my file.txt\" /COPY:DAT /DCOPY:T /IS /IT /R:3 /W:5 /MOV {Out}",
                Cmd(spec, Files, false, Conflict.Overwrite, true), "files, overwrite, move");
            Check(Cmd(spec, Folder, false, Conflict.ReplaceIfNewer, true).Contains(" /XO /R:3 /W:5 /MOVE "), "replace if newer + move folder");
            spec.KeepGoing = false;
            Check(Cmd(spec, Folder, false, Conflict.Skip).Contains("/R:0 /W:0"), "stop at first error");
            Check(Cmd(spec, Folder, false, Conflict.Skip, false, new[] { @"C:\x\big file.iso" }).Contains("/XF \"C:\\x\\big file.iso\""), "skip a file");
            Equal("D:\\", Planner.Build(spec, new RoboTask { SourceDir = @"C:\", DestDir = @"D:\" }, false, Conflict.Skip, false)[1], "drive root keeps its backslash");
        }

        private static void BuildsAdvancedCommands()
        {
            var spec = new JobSpec
            {
                Destination = @"D:\Backup",
                Advanced = true,
                Switches = new List<SwitchValue>
                {
                    new SwitchValue("/MIR"), new SwitchValue("/MT", "16"), new SwitchValue("/COPY", "DATS"),
                    new SwitchValue("/XF", "*.tmp \"Thumbs.db\""), new SwitchValue("/MON", "1"), new SwitchValue("/LOG", @"C:\logs\copy log.txt"),
                    new SwitchValue("/L"), new SwitchValue("/MOVE")
                },
                ExtraOptions = "/XA:H /MOT:5"
            };
            var run = Cmd(spec, Folder, false, Conflict.Overwrite);
            Equal($"\"C:\\Users\\Azeem\\Pictures\\Photos 2026\" \"D:\\Backup\\Photos 2026\" /MIR /MT:16 /COPY:DATS /XF *.tmp Thumbs.db /MON:1 /XA:H /MOT:5 {Out}",
                run, "advanced run (internal /L and /MOVE dropped; KUDUCOPY writes the /LOG file itself)");

            var scan = Cmd(spec, Folder, true, Conflict.Overwrite);
            Check(!scan.Contains("/MON") && !scan.Contains("/MOT") && !scan.Contains("/LOG") && !scan.Contains("/TEE"), "scan drops monitor and log options: " + scan);
            Check(scan.Contains(" /L "), "scan is list-only");

            var files = Cmd(spec, Files, false, Conflict.Overwrite);
            Check(!files.Contains("/MIR") && !files.Contains("/E ") && files.Contains("/MT:16"), "single files never mirror or walk folders: " + files);
        }

        private static void PlansTasks()
        {
            var root = Path.Combine(Path.GetTempPath(), "kuducopy-plan-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(root, "src");
                Directory.CreateDirectory(Path.Combine(src, "Photos"));
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                File.WriteAllText(Path.Combine(src, "b.txt"), "b");
                var dest = Path.Combine(root, "dest");
                var spec = new JobSpec { Destination = dest, Sources = { Path.Combine(src, "Photos"), Path.Combine(src, "a.txt"), Path.Combine(src, "b.txt") } };
                var tasks = Planner.Tasks(spec);
                Equal(2, tasks.Count, "one folder run + one run for the loose files");
                Equal(Path.Combine(dest, "Photos"), tasks[0].DestDir, "folder goes inside destination");
                Equal("a.txt,b.txt", string.Join(",", tasks[1].Files), "files grouped");

                var into = new JobSpec { Destination = Path.Combine(src, "Photos", "inner"), Sources = { Path.Combine(src, "Photos") } };
                Throws(() => Planner.Tasks(into), "into itself");
                var same = new JobSpec { Destination = src, Sources = { Path.Combine(src, "a.txt") } };
                Throws(() => Planner.Tasks(same), "already in");
                var missing = new JobSpec { Destination = dest, Sources = { Path.Combine(src, "nope.txt") } };
                Throws(() => Planner.Tasks(missing), "can't be found");
                Throws(() => Planner.Tasks(new JobSpec { Destination = dest }), "Add some files");

                Equal("Drive D", Planner.DisplayName(@"D:\"), "drive display name");
                Equal("Photos", Planner.DisplayName(@"C:\x\Photos\"), "folder display name");
                Equal(@"\\NAS\PHOTOS", Planner.VolumeKey(@"\\nas\photos\2026\march"), "network volume key");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Throws(Action a, string expectedText)
        {
            try
            {
                a();
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains(expectedText), $"message should contain \"{expectedText}\": {ex.Message}");
                return;
            }
            throw new Exception("expected an error containing: " + expectedText);
        }

        private static void OptionCatalogue()
        {
            Check(RoboOptions.All.Count >= 70, "a full catalogue (" + RoboOptions.All.Count + ")");
            Equal(RoboOptions.All.Count, RoboOptions.All.Select(o => o.Switch.ToUpperInvariant()).Distinct().Count(), "no duplicates");
            Check(RoboOptions.All.All(o => RoboOptions.Groups.Contains(o.Group)), "every option has a known group");
            Check(RoboOptions.All.All(o => !RoboOptions.IsInternal(o.Switch)), "internal switches are not offered");
            Check(RoboOptions.Find("/MIR").Deletes && RoboOptions.Find("/PURGE").Deletes, "deleting options flagged");
            Check(RoboOptions.Find("/B").NeedsAdmin && RoboOptions.Find("/COPYALL").NeedsAdmin, "admin options flagged");

            Equal("/MT:8", string.Join(" ", RoboOptions.ToArgs(new SwitchValue("/MT", ""))), "default value used");
            Equal("/A+:RH", string.Join(" ", RoboOptions.ToArgs(new SwitchValue("/A+", "RH"))), "attributes");
            Equal(null, RoboOptions.Validate(new SwitchValue("/MT", "32")), "valid threads");
            Check(RoboOptions.Validate(new SwitchValue("/MT", "500")) != null, "too many threads");
            Check(RoboOptions.Validate(new SwitchValue("/COPY", "DAQ")) != null, "bad letter");
            Equal(null, RoboOptions.Validate(new SwitchValue("/COPY", "dats")), "letters any case");
            Check(RoboOptions.Validate(new SwitchValue("/RH", "22-6")) != null, "bad run hours");
            Equal(null, RoboOptions.Validate(new SwitchValue("/RH", "2200-0600")), "run hours");
            Check(RoboOptions.Validate(new SwitchValue("/W", "abc")) != null, "number needed");
            Check(RoboOptions.Defaults().Any(d => d.Switch == "/R" && d.Value == "3"), "defaults avoid Robocopy's million retries");
        }

        private static void DetectsSupportedOptions()
        {
            const string help = @"-------------------------------------------------------------------------------
   ROBOCOPY     ::     Robust File Copy for Windows
-------------------------------------------------------------------------------
  Started : Thursday, 22 October 2026 14:00:00
              Usage :: ROBOCOPY source destination [file [file]...] [options]
                 /S :: copy Subdirectories, but not empty ones.
                 /E :: copy subdirectories, including Empty ones.
             /MT[:n] :: Do multi-threaded copies with n threads (default 8).
          /COPY:copyflag[s] :: what to COPY for files (default is /COPY:DAT).
                /MIR :: MIRror a directory tree (equivalent to /E plus /PURGE).
               /PURGE :: delete dest files/dirs that no longer exist in source.
Microsoft Windows [Version 10.0.19045.4529]";
            var supported = RoboOptions.Supported(help);
            Check(supported.Contains("/S") && supported.Contains("/E") && supported.Contains("/MT") && supported.Contains("/MIR"), "found");
            Check(!supported.Contains("/COMPRESS") && !supported.Contains("/SPARSE"), "newer options hidden on older Windows");
            Check(!supported.Contains("/MOT"), "/MOT isn't matched by /MOVE-like text");
            Equal(RoboOptions.All.Count, RoboOptions.Supported("").Count, "unknown help: show everything");
            Equal("10.0.19045.4529", RoboOptions.Version(help), "version");
        }

        private static void ConflictRules()
        {
            var t = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            Equal(Existing.Same, PlannedFile.Compare(100, t, 100, t.AddSeconds(1)), "2-second tolerance");
            Equal(Existing.Different, PlannedFile.Compare(100, t, 90, t), "same time, different size");
            Equal(Existing.SourceNewer, PlannedFile.Compare(100, t.AddHours(1), 100, t), "newer");
            Equal(Existing.SourceOlder, PlannedFile.Compare(100, t, 100, t.AddHours(1)), "older");

            var fresh = new PlannedFile { Existing = Existing.None };
            var newer = new PlannedFile { Existing = Existing.SourceNewer };
            var older = new PlannedFile { Existing = Existing.SourceOlder };
            var same = new PlannedFile { Existing = Existing.Same };
            Check(fresh.CopiedBy(Conflict.Skip) && !newer.CopiedBy(Conflict.Skip), "skip");
            Check(newer.CopiedBy(Conflict.ReplaceIfNewer) && !older.CopiedBy(Conflict.ReplaceIfNewer) && !same.CopiedBy(Conflict.ReplaceIfNewer), "replace if newer");
            Check(older.CopiedBy(Conflict.Overwrite) && same.CopiedBy(Conflict.Overwrite), "overwrite");

            Equal("/XO", string.Join(" ", Planner.PolicyArgs(Conflict.ReplaceIfNewer)), "newer args");
            Equal("/XC /XN /XO", string.Join(" ", Planner.PolicyArgs(Conflict.KeepBoth)), "keep both lets robocopy copy only new files");
        }

        private static void KeepBothNames()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("D", "photo (2).jpg") };
            Equal(Path.Combine("D", "photo (3).jpg"), PlannedFile.KeepBothName(Path.Combine("D", "photo.jpg"), taken.Contains), "next free number");
            Equal(Path.Combine("D", "notes (2)"), PlannedFile.KeepBothName(Path.Combine("D", "notes"), p => false), "no extension");
        }

        private static void ChecksumValues()
        {
            var data = Encoding.ASCII.GetBytes("123456789");
            Equal("cbf43926", Checksums.HashBytes(data, HashKind.Crc32), "CRC32 check value");
            Equal("25f9e794323b453885f5181f1b624d0b", Checksums.HashBytes(data, HashKind.Md5), "MD5");
            Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", Checksums.HashBytes(data, HashKind.Sha1), "SHA-1");
            Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", Checksums.HashBytes(data, HashKind.Sha256), "SHA-256");

            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, data);
                long read = 0;
                Equal("cbf43926", Checksums.HashFile(file, HashKind.Crc32, n => read += n), "CRC32 of a file");
                Equal(9L, read, "progress reported");
            }
            finally
            {
                File.Delete(file);
            }
        }

        private static void ChecksumFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "kuducopy-sums-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var sep = Path.DirectorySeparatorChar;
                var a = Path.Combine(dir, "a.txt");
                var md5 = Checksums.Write(dir, HashKind.Md5, new[] { new KeyValuePair<string, string>(a, "abc123") }, "sums");
                Check(md5.EndsWith("sums.md5"), "md5 extension");
                var text = File.ReadAllText(md5);
                Check(text.Contains("abc123 *a.txt"), "md5sum style: " + text);
                var sfv = Checksums.Write(dir, HashKind.Crc32, new[] { new KeyValuePair<string, string>(a, "cbf43926") }, "sums");
                Check(File.ReadAllText(sfv).Contains("a.txt CBF43926"), "sfv style");
                _ = sep;
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void QuotesArguments()
        {
            Equal("\"C:\\x y\\\\\"", Args.Quote("C:\\x y\\"), "trailing backslash doubled inside quotes");
            Equal("D:\\", Args.Quote("D:\\"), "no quotes needed");
            Equal("*.tmp|Thumbs.db|my file.txt", string.Join("|", Args.Split("*.tmp  Thumbs.db \"my file.txt\"")), "split");
            Equal(0, Args.Split("   ").Count, "empty");
        }

        private static void ParsesCommandLine()
        {
            var c = CommandLine.Parse(new[] { "--paste", @"D:\Backup" });
            Equal(Launch.Paste, c.Action, "paste");
            var t = CommandLine.Parse(new[] { "--copy-to", @"D:\Backup", @"C:\a.txt", @"C:\b.txt" });
            Equal(Launch.CopyTo, t.Action, "copy to");
            Equal(2, t.Paths.Count, "paths");
            Equal(Launch.MoveToAsk, CommandLine.Parse(new[] { "--move-to-ask", @"C:\a.txt" }).Action, "move to ask");
            Equal(Launch.Open, CommandLine.Parse(new[] { "--paste" }).Action, "paste without folder opens the window");
            Check(CommandLine.Parse(new[] { "--register-shell" }).IsSetupOnly, "installer hook");

            var other = CommandLine.Parse(new[] { "--copy-to", @"D:\Backup", @"C:\b.txt", @"C:\c.txt" });
            Equal(t.BatchKey, other.BatchKey, "same batch");
            t.Merge(other);
            Equal(3, t.Paths.Count, "merged without duplicates");
            Equal("--copy-to", CommandLine.Parse(t.ToArgs()).ToArgs()[0], "round trip");
        }

        private static void ShellEntries()
        {
            var e = ShellIntegration.Entries(@"C:\Apps\KUDUCOPY\KUDUCOPY.exe", new List<string> { @"D:\Backup", @"\\nas\photos" });
            Equal("\"C:\\Apps\\KUDUCOPY\\KUDUCOPY.exe\" --paste \"%V\"", e[@"Directory\Background\shell\KUDUCOPY.Paste\command"][""], "paste in folder background");
            Equal("Paste with KUDUCOPY", e[@"Directory\Background\shell\KUDUCOPY.Paste"]["MUIVerb"], "label");
            Equal("", e[@"*\shell\KUDUCOPY.CopyTo"]["SubCommands"], "cascading menu");
            Equal("\"C:\\Apps\\KUDUCOPY\\KUDUCOPY.exe\" --copy-to \"D:\\Backup\" \"%1\"", e[@"*\shell\KUDUCOPY.CopyTo\shell\00\command"][""], "favourite");
            Equal("\"C:\\Apps\\KUDUCOPY\\KUDUCOPY.exe\" --move-to-ask \"%1\"", e[@"Directory\shell\KUDUCOPY.MoveTo\shell\99\command"][""], "choose a folder");
            Check(e.Keys.All(k => !k.StartsWith("HKEY", StringComparison.Ordinal)), "relative to Software\\Classes");
        }

        private static void HistoryAndRetry()
        {
            var root = Path.Combine(Path.GetTempPath(), "kuducopy-hist-" + Guid.NewGuid().ToString("N"));
            AppPaths.Root = root;
            try
            {
                var spec = new JobSpec { Destination = @"D:\Backup", Sources = { @"C:\Photos" }, Verify = true, Hash = HashKind.Sha256 };
                var record = new JobRecord
                {
                    Started = DateTime.Now, Mode = CopyMode.Copy, From = "Photos", To = @"D:\Backup", Files = 3, Copied = 1,
                    Result = JobResult.DoneWithProblems, Spec = spec,
                    Failed =
                    {
                        new FailedFile { Source = P("C", "Photos", "March", "Thumbs.db"), Dest = P("D", "Photos", "March", "Thumbs.db"), Reason = "In use by another program" },
                        new FailedFile { Source = P("C", "Photos", "desktop.ini"), Dest = P("D", "Photos", "desktop.ini"), Reason = "Access is denied" },
                        new FailedFile { Source = P("C", "Photos", "a.jpg"), Dest = P("D", "Photos", "a (2).jpg"), Reason = "Access is denied" }
                    }
                };
                History.Save(record);
                var list = History.List();
                Equal(1, list.Count, "saved");
                Equal("3 failed", list[0].ResultText, "result text");
                Equal(HashKind.Sha256, list[0].Spec.Hash, "spec saved");

                var retry = Retry.For(list[0].Spec, list[0].Failed);
                Equal(Conflict.Overwrite, retry.Conflict, "retry replaces half-copied files");
                Equal(2, retry.Tasks.Count, "grouped by folder, renamed keep-both copy left out");
                Check(retry.Tasks.Any(t => t.SourceDir.EndsWith("March") && t.Files.Single() == "Thumbs.db"), "retry task");
                Equal("Thumbs.db and desktop.ini", retry.SourceSummary, "summary");

                History.Delete(list[0]);
                Equal(0, History.List().Count, "deleted");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string P(params string[] parts) => Path.Combine(parts);

        private static void MirrorRules()
        {
            var spec = new JobSpec { Destination = @"D:\Backup", Mode = CopyMode.Mirror };
            Equal($"\"C:\\Users\\Azeem\\Pictures\\Photos 2026\" \"D:\\Backup\\Photos 2026\" /MIR /COPY:DAT /DCOPY:T /R:3 /W:5 {Out}",
                Cmd(spec, Folder, false, Conflict.Skip), "mirror run");
            Equal($"\"C:\\Users\\Azeem\\Pictures\\Photos 2026\" \"D:\\Backup\\Photos 2026\" /MIR /COPY:DAT /DCOPY:T /R:3 /W:5 /L {Out}",
                Cmd(spec, Folder, true, Conflict.Skip), "mirror scan lists copies and deletes, not identical files");
            Check(spec.RobocopyDecides && spec.Deletes, "mirror: robocopy decides, files may be deleted");
            Equal("Mirroring", spec.Verbing, "verb");

            var adv = new JobSpec { Destination = @"D:\Backup", Mode = CopyMode.Mirror, Advanced = true, Switches = { new SwitchValue("/MT", "8") } };
            Check(Cmd(adv, Folder, false, Conflict.Overwrite).Contains("/MT:8 /MIR"), "advanced mirror adds /MIR");
            Check(!new JobSpec { Advanced = true }.Deletes, "advanced without /MIR doesn't delete");
            Check(new JobSpec { Advanced = true, Switches = { new SwitchValue("/PURGE") } }.Deletes, "/PURGE deletes");

            var root = Path.Combine(Path.GetTempPath(), "kuducopy-mirror-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Photos"));
                File.WriteAllText(Path.Combine(root, "a.txt"), "a");
                var ok = Planner.Tasks(new JobSpec { Mode = CopyMode.Mirror, Destination = Path.Combine(root, "dest"), Sources = { Path.Combine(root, "Photos") } });
                Equal(Path.Combine(root, "dest", "Photos"), ok[0].DestDir, "mirror into a folder of the same name, never the destination itself");
                Throws(() => Planner.Tasks(new JobSpec { Mode = CopyMode.Mirror, Destination = Path.Combine(root, "dest"), Sources = { Path.Combine(root, "a.txt") } }), "Mirror works with folders");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            var files = new List<PlannedFile> { new PlannedFile { Source = "a", Existing = Existing.None }, new PlannedFile { Source = "b", Existing = Existing.SourceOlder } };
            var preview = PreviewResult.Build(spec, files, new[] { new PlannedFile { Dest = "x" } });
            Equal("Copy,Replace,Delete", string.Join(",", preview.Entries.Select(e => e.Action)), "mirror preview");
            Equal("Done · 3 deleted", new JobRecord { Result = JobResult.Done, Deleted = 3 }.ResultText, "history text");
        }

        private static void PreviewSorts()
        {
            var files = new List<PlannedFile>
            {
                new PlannedFile { Source = "a", Size = 10, Existing = Existing.None },
                new PlannedFile { Source = "b", Size = 20, Existing = Existing.SourceNewer },
                new PlannedFile { Source = "c", Size = 30, Existing = Existing.SourceOlder },
                new PlannedFile { Source = "d", Size = 40, Existing = Existing.Same }
            };
            string Actions(PreviewResult r) => string.Join(",", r.Entries.Select(e => e.Action));

            Equal("Copy,Replace,Skip,Skip", Actions(PreviewResult.Build(new JobSpec { Conflict = Conflict.ReplaceIfNewer }, files, null)), "replace if newer");
            Equal("Copy,Replace,Replace,Replace", Actions(PreviewResult.Build(new JobSpec { Conflict = Conflict.Overwrite }, files, null)), "overwrite");
            Equal("Copy,Skip,Skip,Skip", Actions(PreviewResult.Build(new JobSpec { Conflict = Conflict.Skip }, files, null)), "skip");
            Equal("Copy,KeepBoth,KeepBoth,KeepBoth", Actions(PreviewResult.Build(new JobSpec { Conflict = Conflict.KeepBoth }, files, null)), "keep both");
            var ask = PreviewResult.Build(new JobSpec { Conflict = Conflict.Ask }, files, null);
            Equal("Copy,Ask,Ask,Ask", Actions(ask), "ask");
            Equal(100L, ask.BytesToWrite, "at most everything is written when asking");

            var mirror = PreviewResult.Build(new JobSpec { Advanced = true }, files.Take(2),
                new[] { new PlannedFile { Dest = @"D:\x\old.txt", Size = 5 } });
            Equal("Copy,Replace,Delete", Actions(mirror), "advanced: listed files are copied, extras deleted");
            Equal(1, mirror.Count(PreviewAction.Delete), "one delete");
            Equal(5L, mirror.Bytes(PreviewAction.Delete), "delete size");
            Equal("Yours is newer", mirror.Entries[1].Note, "note");
        }

        private static void ReadsJobFiles()
        {
            var rcj = new[]
            {
                ":: Robocopy Job C:\\jobs\\nightly.RCJ",
                ":: Source Directory :",
                "\t/SD:C:\\Users\\Azeem\\Pictures\\\t:: Source Directory.",
                "\t/DD:D:\\Backup\\Pictures\\\t:: Destination Directory.",
                "\t/IF\t\t:: Include Files matching these names",
                "\t\t*.*",
                "\t/XD\t\t:: eXclude Directories matching these names",
                "\t\tcache",
                "\t\tmy temp",
                "\t/XF\t\t:: eXclude Files matching these names",
                "\t\t*.tmp",
                "\t/E\t\t:: copy subdirectories, including Empty ones.",
                "\t/COPY:DAT\t:: what to COPY for files (default is /COPY:DAT).",
                "\t/MT:16\t:: Do multi-threaded copies with n threads.",
                "\t/R:2\t:: number of Retries on failed copies.",
                "\t/NJH\t:: No Job Header.",
                "\t/FUTURE:1\t:: an option from a newer Windows"
            };
            var job = JobFile.Parse(rcj);
            Equal(@"C:\Users\Azeem\Pictures", job.Source, "source");
            Equal(@"D:\Backup\Pictures", job.Destination, "destination");
            Equal("/XD|/XF|/E|/COPY|/MT|/R", string.Join("|", job.Switches.Select(s => s.Switch)), "switches (internal /NJH and /IF dropped)");
            Equal("cache \"my temp\"", job.Switches.Single(s => s.Switch == "/XD").Value, "list items quoted");
            Equal("16", job.Switches.Single(s => s.Switch == "/MT").Value, "value");
            Equal("/FUTURE:1", string.Join(" ", job.Unknown), "unknown kept as extra options");
        }

        private static void FormatsSizes()
        {
            Equal("512 B", Format.Size(512), "bytes");
            Equal("1.5 KB", Format.Size(1536), "KB");
            Equal("21.5 GB", Format.Size(23085449216), "GB");
            Equal("186 MB/s", Format.Speed(186L * 1024 * 1024), "speed");
            Equal("1 min 4 s", Format.Duration(TimeSpan.FromSeconds(64)), "duration");
        }

        private static void CompareFolders()
        {
            var root = Path.Combine(Path.GetTempPath(), "kuducopy-compare-" + Guid.NewGuid().ToString("N"));
            var left = Path.Combine(root, "Left");
            var right = Path.Combine(root, "Right");
            try
            {
                void Put(string dir, string rel, string text, DateTime when)
                {
                    var file = Path.Combine(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, text);
                    File.SetLastWriteTimeUtc(file, when);
                }
                var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
                Put(left, "same.txt", "same", t);
                Put(right, "same.txt", "same", t.AddSeconds(1));
                Put(left, P("sub", "only left.txt"), "L", t);
                Put(right, "only right.txt", "R", t);
                Put(left, P("sub", "newer.txt"), "new text", t.AddHours(1));
                Put(right, P("sub", "newer.txt"), "old", t);
                Put(left, "older.txt", "a", t);
                Put(right, "older.txt", "b", t.AddDays(1));
                Put(left, "size.txt", "short", t);
                Put(right, "size.txt", "longer", t);
                Put(left, "content.txt", "abc", t);
                Put(right, "content.txt", "xyz", t);
                Put(left, "touched.txt", "same data", t);
                Put(right, "touched.txt", "same data", t.AddHours(3));

                var quick = FolderCompare.Run(left, right, false);
                CompareState S(CompareResult r, string rel) => r.Entries.Single(e => e.Relative == rel).State;
                Equal(8, quick.Entries.Count, "files listed");
                Equal(CompareState.Same, S(quick, "same.txt"), "within 2 seconds");
                Equal(CompareState.LeftOnly, S(quick, P("sub", "only left.txt")), "left only");
                Equal(CompareState.RightOnly, S(quick, "only right.txt"), "right only");
                Equal(CompareState.LeftNewer, S(quick, P("sub", "newer.txt")), "left newer");
                Equal(CompareState.RightNewer, S(quick, "older.txt"), "right newer");
                Equal(CompareState.Different, S(quick, "size.txt"), "same date, other size");
                Equal(CompareState.Same, S(quick, "content.txt"), "size and date can't see changed bytes");
                Equal(CompareState.RightNewer, S(quick, "touched.txt"), "touched date");
                Check(!quick.Identical, "not identical");
                Equal(CompareState.LeftOnly, quick.Entries[0].State, "sorted by state");

                var deep = FolderCompare.Run(left, right, true);
                Equal(CompareState.Different, S(deep, "content.txt"), "contents differ");
                Equal(CompareState.Same, S(deep, "touched.txt"), "same contents, other date");
                Check(deep.Entries.Single(e => e.Relative == "touched.txt").SameContent, "flagged as same content");
                Equal(CompareState.RightNewer, S(deep, "older.txt"), "different contents keep newer side");

                var picked = quick.Entries.Where(e => e.State != CompareState.Same).ToList();
                var toRight = FolderCompare.CopySpec(quick, picked, true);
                Equal(right, toRight.Destination, "copies into the right folder");
                Equal(Conflict.Overwrite, toRight.Conflict, "replaces what's there");
                var top = toRight.Tasks.Single(x => x.SourceDir == left);
                Equal(right, top.DestDir, "top level files");
                Check(top.Files.Contains("older.txt") && top.Files.Contains("size.txt") && !top.Files.Contains("only right.txt"), "left files only: " + string.Join(",", top.Files));
                var sub = toRight.Tasks.Single(x => x.SourceDir == Path.Combine(left, "sub"));
                Equal(Path.Combine(right, "sub"), sub.DestDir, "same subfolder on the other side");
                Equal(2, sub.Files.Count, "files in sub");
                Equal(2, FolderCompare.ReplacesNewer(picked, true), "two newer files would be replaced");
                var toLeft = FolderCompare.CopySpec(quick, picked, false);
                Check(toLeft.Tasks.SelectMany(x => x.Files).Contains("only right.txt") && !toLeft.Tasks.SelectMany(x => x.Files).Contains("only left.txt"), "right to left");
                Check(FolderCompare.Report(quick).Contains("Only on the left\t" + P("sub", "only left.txt")), "report");

                var threw = false;
                try { FolderCompare.Run(left, Path.Combine(left, "sub"), false); }
                catch (InvalidOperationException) { threw = true; }
                Check(threw, "nested folders rejected");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
