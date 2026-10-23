using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    /// <summary>
    /// KUDUCOPY.exe --self-test: real copies with robocopy.exe in a temporary folder (conflicts, keep both, move, verify,
    /// a locked file and retry, advanced options, cancel), then every window in both themes. Writes self-test.log next to
    /// the exe and exits with 0 when everything passed. Used by the GitHub build. Never touches the user's files or History.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        /// <summary>The answer the self-test gives when a job asks about existing files.</summary>
        public static ConflictAnswer ConflictAnswer { get; set; }
        public static Conflict DecideEach { get; set; } = Conflict.Skip;

        private static StringBuilder _log;
        private static string _temp;

        public static int Run()
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kuducopy-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            AppPaths.Root = Path.Combine(_temp, "appdata");

            Step("Robocopy is available", () => Expect(File.Exists(CopyJob.RobocopyPath), "robocopy.exe at " + CopyJob.RobocopyPath));
            Step("Copy a folder tree with verify", CopyTree);
            Step("Existing files: replace if newer", ReplaceIfNewer);
            Step("Existing files: keep both", KeepBoth);
            Step("Existing files: decide per file", DecidePerFile);
            Step("Move with verify", MoveWithVerify);
            Step("Move with robocopy", MoveWithRobocopy);
            Step("Locked file fails, retry succeeds", LockedFileAndRetry);
            Step("Advanced options (/XF, /MIR)", AdvancedOptions);
            Step("Cancel a copy", CancelCopy);
            Step("Single files", SingleFiles);
            Step("Preview: standard and /MIR", PreviewJob);
            Step("Mirror a folder", MirrorFolder);
            Step("Compare two folders, copy the differences", CompareFolders);

            Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
            Step("Theme keys exist", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "Text", "SubtleText", "Accent", "OkBg", "BadText", "MonoBg", "UiFont", "MonoFont",
                                            "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "Card", "PresetRadio", "SegmentRadio",
                                            "ChipRadio", "Switch", "TableList", "MonoBox", "IconCopy", "IconMove", "IconPause", "IconSkip", "IconStar",
                                            "IconDrive", "IconUp", "IconDown", "IconLog", "IconTrash", "IconFolder", "IconFile", "IconPlus" })
                    Application.Current.FindResource(key);
            });

            MainWindow main = null;
            Step("Main window, standard mode", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                Application.Current.MainWindow = main;
                main.Show();
                Pump();
                main.AddSources(new[] { Folder("ui-src") });
                main.Draft.Destination = Path.Combine(_temp, "ui-dest");
            });
            Step("Main window, advanced mode", () =>
            {
                main.SelectAdvanced(true);
                Pump();
                main.SelectAdvanced(false);
            });
            Step("Job window: a real copy through the UI", () =>
            {
                var spec = new JobSpec { Sources = { Folder("ui-src") }, Destination = Path.Combine(_temp, "ui-dest"), Verify = true };
                var window = new JobWindow(new CopyJob(spec), main);
                OffScreen(window);
                window.Show();
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!window.IsDone && DateTime.UtcNow < until) Pump();
                Expect(window.IsDone, "job window finished");
                Expect(window.Job.Record.Result == JobResult.Done, "result " + window.Job.Record.Result + " " + window.Job.Record.Message);
                Pump();
                window.Close();
            });
            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                Pump();
                main.SelectAdvanced(true);
                Pump();
                main.SelectAdvanced(false);
                ThemeManager.Apply(ThemeMode.Light);
            });
            Step("History window", () => ShowAndClose(new HistoryWindow(main)));
            Step("Favourites window", () => ShowAndClose(new FavouritesWindow(main)));
            Step("Settings window", () => ShowAndClose(new SettingsWindow(main, () => "10.0")));
            Step("Preview window", () =>
            {
                var w = new PreviewWindow(new JobSpec { Sources = { Folder("ui-src") }, Destination = Path.Combine(_temp, "ui-dest"), Conflict = Conflict.ReplaceIfNewer }, main);
                OffScreen(w);
                w.Show();
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!w.IsReady && DateTime.UtcNow < until) Pump();
                Expect(w.IsReady, "preview finished");
                Expect(w.Result.Count(PreviewAction.Skip) == 4, "everything already there is skipped, got " + w.Result.Count(PreviewAction.Skip));
                Pump();
                w.Close();
            });
            Step("Compare window", () =>
            {
                var w = new CompareWindow(main, Folder("ui-src"), Path.Combine(_temp, "ui-dest", "ui-src"));
                OffScreen(w);
                w.Show();
                var until = DateTime.UtcNow.AddSeconds(30);
                while ((w.Result == null || w.IsBusy) && DateTime.UtcNow < until) Pump();
                Expect(w.Result != null, "comparison finished");
                Expect(w.Result.Identical, "the copied folder matches, got " + string.Join(", ", w.Result.Entries.Where(e => e.State != CompareState.Same).Select(e => e.Relative)));
                Pump();
                w.Close();
            });
            Step("Close main window", () => main?.Close());

            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); } catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex.Message);
            }
        }

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        // ================================================================== real copies

        /// <summary>Creates (once) a small folder tree to copy.</summary>
        private static string Folder(string name)
        {
            var root = Path.Combine(_temp, name);
            if (Directory.Exists(root)) return root;
            Directory.CreateDirectory(Path.Combine(root, "March", "Week 1"));
            Directory.CreateDirectory(Path.Combine(root, "Empty folder"));
            File.WriteAllText(Path.Combine(root, "notes.txt"), "Hello from KUDUCOPY");
            File.WriteAllText(Path.Combine(root, "Ünïcødé ファイル.txt"), "Unicode names survive");
            File.WriteAllText(Path.Combine(root, "March", "Week 1", "deep file.csv"), "a,b,c\n1,2,3");
            File.WriteAllBytes(Path.Combine(root, "March", "photo.bin"), Enumerable.Range(0, 300000).Select(i => (byte)(i * 7)).ToArray());
            return root;
        }

        private static CopyJob RunJob(JobSpec spec, Action<CopyJob> whileRunning = null)
        {
            var job = new CopyJob(spec)
            {
                AskConflicts = q => ConflictAnswer ?? new ConflictAnswer { Policy = Conflict.Skip },
                DecideEach = list => list.ToDictionary(f => f, f => DecideEach)
            };
            var task = job.RunAsync();
            whileRunning?.Invoke(job);
            if (!task.Wait(TimeSpan.FromSeconds(120))) throw new Exception("job didn't finish within 2 minutes");
            _log.AppendLine($"      {job.Record.Result}: {job.Record.Copied} copied, {job.Record.Failed.Count} failed, {job.Record.Skipped} skipped. {job.Record.Message}");
            return job;
        }

        private static string Read(string path) => File.ReadAllText(path);

        private static void CopyTree()
        {
            var src = Folder("tree");
            var dest = Path.Combine(_temp, "out1");
            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Verify = true, Hash = HashKind.Sha256 });
            var root = Path.Combine(dest, "tree");
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
            Expect(job.Record.Copied == 4 && job.Record.Verified, "4 files copied and verified");
            Expect(Read(Path.Combine(root, "Ünïcødé ファイル.txt")) == "Unicode names survive", "unicode file copied");
            Expect(Read(Path.Combine(root, "March", "Week 1", "deep file.csv")).StartsWith("a,b,c"), "deep file copied");
            Expect(Directory.Exists(Path.Combine(root, "Empty folder")), "empty folder created");
            Expect(job.Hashes.Count == 4, "4 checksums");
            var sums = Checksums.Write(root, HashKind.Sha256, job.Hashes, "sums");
            Expect(File.ReadAllText(sums).Contains("*March\\photo.bin"), "relative paths in checksum file");
            Expect(File.Exists(job.Record.LogPath) && History.List().Count >= 1, "log and history saved");

            // Same copy again: everything is already there.
            var again = RunJob(new JobSpec { Sources = { src }, Destination = dest, Conflict = Conflict.Skip });
            Expect(again.Record.Result == JobResult.Done && again.Record.Copied == 0 && again.Record.Skipped == 4, "second copy skips everything");
        }

        private static void ReplaceIfNewer()
        {
            var src = Folder("newer");
            var dest = Path.Combine(_temp, "out2");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            var srcNotes = Path.Combine(src, "notes.txt");
            File.WriteAllText(srcNotes, "Newer text");
            File.SetLastWriteTimeUtc(srcNotes, DateTime.UtcNow.AddHours(1));
            var destOther = Path.Combine(dest, "newer", "Ünïcødé ファイル.txt");
            File.WriteAllText(destOther, "Changed in the destination");
            File.SetLastWriteTimeUtc(destOther, DateTime.UtcNow.AddHours(2));

            ConflictAnswer = new ConflictAnswer { Policy = Conflict.ReplaceIfNewer };
            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Conflict = Conflict.Ask });
            ConflictAnswer = null;
            Expect(job.Record.Copied == 1, "only the newer file copied, got " + job.Record.Copied);
            Expect(Read(Path.Combine(dest, "newer", "notes.txt")) == "Newer text", "newer file replaced");
            Expect(Read(destOther) == "Changed in the destination", "file that's newer in the destination kept");
        }

        private static void KeepBoth()
        {
            var src = Folder("both");
            var dest = Path.Combine(_temp, "out3");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            File.WriteAllText(Path.Combine(src, "notes.txt"), "Second version");
            File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddHours(1));
            File.WriteAllText(Path.Combine(src, "brand new.txt"), "New");

            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Conflict = Conflict.KeepBoth });
            var folder = Path.Combine(dest, "both");
            Expect(Read(Path.Combine(folder, "notes.txt")) == "Hello from KUDUCOPY", "original kept");
            Expect(Read(Path.Combine(folder, "notes (2).txt")) == "Second version", "copy kept as notes (2).txt");
            Expect(File.Exists(Path.Combine(folder, "brand new.txt")), "new file copied by robocopy");
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
        }

        private static void DecidePerFile()
        {
            var src = Folder("decide");
            var dest = Path.Combine(_temp, "out4");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            File.WriteAllText(Path.Combine(src, "notes.txt"), "Decided");
            File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddHours(1));
            DecideEach = Conflict.Overwrite;
            ConflictAnswer = new ConflictAnswer { Policy = Conflict.Decide };
            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Conflict = Conflict.Ask });
            ConflictAnswer = null;
            DecideEach = Conflict.Skip;
            Expect(Read(Path.Combine(dest, "decide", "notes.txt")) == "Decided", "replaced by choice");
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
        }

        private static void MoveWithVerify()
        {
            var src = Folder("move-verify");
            var dest = Path.Combine(_temp, "out5");
            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Mode = CopyMode.Move, Verify = true });
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
            Expect(!Directory.Exists(src), "source folder removed after verifying");
            Expect(Read(Path.Combine(dest, "move-verify", "notes.txt")) == "Hello from KUDUCOPY", "moved");
        }

        private static void MoveWithRobocopy()
        {
            var src = Folder("move-robo");
            var dest = Path.Combine(_temp, "out6");
            var job = RunJob(new JobSpec { Sources = { src }, Destination = dest, Mode = CopyMode.Move });
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
            Expect(!File.Exists(Path.Combine(src, "notes.txt")), "source files moved");
            Expect(File.Exists(Path.Combine(dest, "move-robo", "March", "Week 1", "deep file.csv")), "moved deep file");
        }

        private static void LockedFileAndRetry()
        {
            var src = Folder("locked");
            var dest = Path.Combine(_temp, "out7");
            var lockedPath = Path.Combine(src, "notes.txt");
            var spec = new JobSpec
            {
                Sources = { src }, Destination = dest, Advanced = true,
                Switches = new List<SwitchValue> { new SwitchValue("/E"), new SwitchValue("/R", "0"), new SwitchValue("/W", "0") }
            };
            CopyJob job;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                job = RunJob(spec);
            Expect(job.Record.Result == JobResult.DoneWithProblems, "done with problems, got " + job.Record.Result);
            Expect(job.Record.Failed.Count == 1 && job.Record.Failed[0].Source.EndsWith("notes.txt"), "the locked file failed");
            Expect(job.Record.Failed[0].Reason.Length > 0, "reason given: " + job.Record.Failed[0].Reason);
            Expect(job.Record.Copied == 3, "the other files copied");

            var retry = RunJob(Retry.For(job.Spec, job.Record.Failed));
            Expect(retry.Record.Result == JobResult.Done && retry.Record.Copied == 1, "retry copied the file");
            Expect(Read(Path.Combine(dest, "locked", "notes.txt")) == "Hello from KUDUCOPY", "retried file correct");
        }

        private static void AdvancedOptions()
        {
            var src = Folder("advanced");
            File.WriteAllText(Path.Combine(src, "skip me.tmp"), "temp");
            var dest = Path.Combine(_temp, "out8");
            var extra = Path.Combine(dest, "advanced", "only in destination.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(extra));
            File.WriteAllText(extra, "will be removed by /MIR");
            var spec = new JobSpec
            {
                Sources = { src }, Destination = dest, Advanced = true,
                Switches = new List<SwitchValue>
                {
                    new SwitchValue("/MIR"), new SwitchValue("/XF", "*.tmp"), new SwitchValue("/MT", "4"), new SwitchValue("/R", "1"), new SwitchValue("/W", "1"),
                    new SwitchValue("/UNILOG", Path.Combine(_temp, "my log.txt"))
                }
            };
            var job = RunJob(spec);
            Expect(File.Exists(Path.Combine(_temp, "my log.txt")) && File.ReadAllText(Path.Combine(_temp, "my log.txt")).Contains("photo.bin"), "/UNILOG file written");
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
            Expect(!File.Exists(Path.Combine(dest, "advanced", "skip me.tmp")), "/XF skipped *.tmp");
            Expect(!File.Exists(extra), "/MIR removed the extra file");
            Expect(File.Exists(Path.Combine(dest, "advanced", "March", "photo.bin")), "files copied with /MT");
        }

        private static void CancelCopy()
        {
            var src = Path.Combine(_temp, "big");
            Directory.CreateDirectory(src);
            var data = new byte[1 << 20];
            for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(src, $"part {i:00}.bin"), data);
            var dest = Path.Combine(_temp, "out9");
            var spec = new JobSpec
            {
                Sources = { src }, Destination = dest, Advanced = true,
                Switches = new List<SwitchValue> { new SwitchValue("/E"), new SwitchValue("/IPG", "300"), new SwitchValue("/R", "0"), new SwitchValue("/W", "0") }
            };
            var job = RunJob(spec, j =>
            {
                var until = DateTime.UtcNow.AddSeconds(20);
                while (j.Phase != JobPhase.Copying && DateTime.UtcNow < until) Thread.Sleep(50);
                Thread.Sleep(1500);
                j.Pause();
                Thread.Sleep(500);
                j.Resume();
                j.Cancel();
            });
            Expect(job.Record.Result == JobResult.Cancelled, "cancelled, got " + job.Record.Result);
            var copied = Directory.Exists(Path.Combine(dest, "big")) ? Directory.GetFiles(Path.Combine(dest, "big")).Length : 0;
            Expect(copied < 40, "stopped before the end (" + copied + " of 40)");
            foreach (var f in Directory.Exists(Path.Combine(dest, "big")) ? Directory.GetFiles(Path.Combine(dest, "big")) : new string[0])
                Expect(new FileInfo(f).Length == data.Length, "no half-copied file left: " + Path.GetFileName(f));
        }

        private static void SingleFiles()
        {
            var src = Folder("single");
            var dest = Path.Combine(_temp, "out10");
            var job = RunJob(new JobSpec { Sources = { Path.Combine(src, "notes.txt"), Path.Combine(src, "March", "photo.bin") }, Destination = dest });
            Expect(job.Record.Copied == 2, "2 files copied, got " + job.Record.Copied);
            Expect(File.Exists(Path.Combine(dest, "notes.txt")) && File.Exists(Path.Combine(dest, "photo.bin")), "copied straight into the folder");
            Expect(!Directory.Exists(Path.Combine(dest, "March")), "no folders walked for single files");
        }

        private static void PreviewJob()
        {
            var src = Folder("preview");
            var dest = Path.Combine(_temp, "out11");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            File.WriteAllText(Path.Combine(src, "notes.txt"), "Newer");
            File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddHours(1));
            File.WriteAllText(Path.Combine(src, "added.txt"), "New");
            var extra = Path.Combine(dest, "preview", "extra.txt");
            File.WriteAllText(extra, "only here");

            var standard = new CopyJob(new JobSpec { Sources = { src }, Destination = dest, Conflict = Conflict.ReplaceIfNewer }).PreviewAsync().Result;
            Expect(standard.Count(PreviewAction.Copy) == 1, "1 new file, got " + standard.Count(PreviewAction.Copy));
            Expect(standard.Count(PreviewAction.Replace) == 1, "1 newer file replaced, got " + standard.Count(PreviewAction.Replace));
            Expect(standard.Count(PreviewAction.Skip) == 3, "3 identical files skipped, got " + standard.Count(PreviewAction.Skip));
            Expect(standard.Count(PreviewAction.Delete) == 0, "no deletes in standard mode");
            Expect(Read(Path.Combine(dest, "preview", "notes.txt")) == "Hello from KUDUCOPY", "preview copied nothing");

            var mirror = new CopyJob(new JobSpec
            {
                Sources = { src }, Destination = dest, Advanced = true,
                Switches = new List<SwitchValue> { new SwitchValue("/MIR"), new SwitchValue("/R", "0"), new SwitchValue("/W", "0") }
            }).PreviewAsync().Result;
            Expect(mirror.Count(PreviewAction.Delete) == 1 && mirror.Entries.Single(e => e.Action == PreviewAction.Delete).Dest == extra,
                "the extra file is listed for deletion, got " + mirror.Count(PreviewAction.Delete));
            Expect(File.Exists(extra), "preview deleted nothing");
            _log.AppendLine($"      Preview: {standard.Count(PreviewAction.Copy)} new, {standard.Count(PreviewAction.Replace)} replace, {standard.Count(PreviewAction.Skip)} skip; /MIR: {mirror.Count(PreviewAction.Delete)} delete");
        }

        private static void MirrorFolder()
        {
            var src = Folder("mirror");
            var dest = Path.Combine(_temp, "out12");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            File.WriteAllText(Path.Combine(src, "notes.txt"), "Changed");
            File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddHours(1));
            var extra = Path.Combine(dest, "mirror", "March", "only here.txt");
            File.WriteAllText(extra, "will be removed");

            var spec = new JobSpec { Sources = { src }, Destination = dest, Mode = CopyMode.Mirror };
            var preview = new CopyJob(spec).PreviewAsync().Result;
            Expect(preview.Count(PreviewAction.Delete) == 1, "mirror preview lists 1 delete, got " + preview.Count(PreviewAction.Delete));
            Expect(File.Exists(extra), "preview deleted nothing");

            var job = RunJob(spec);
            Expect(job.Record.Result == JobResult.Done, "Done, got " + job.Record.Result + ": " + job.Record.Message);
            Expect(!File.Exists(extra), "extra file deleted");
            Expect(job.Record.Deleted == 1, "1 deleted, got " + job.Record.Deleted);
            Expect(Read(Path.Combine(dest, "mirror", "notes.txt")) == "Changed", "changed file replaced");

            var rejected = false;
            try { Planner.Tasks(new JobSpec { Sources = { Path.Combine(src, "notes.txt") }, Destination = dest, Mode = CopyMode.Mirror }); }
            catch (Exception) { rejected = true; }
            Expect(rejected, "mirror rejects single files");
        }

        private static void CompareFolders()
        {
            var src = Folder("compare");
            var dest = Path.Combine(_temp, "out13");
            RunJob(new JobSpec { Sources = { src }, Destination = dest });
            var right = Path.Combine(dest, "compare");
            File.WriteAllText(Path.Combine(src, "March", "Week 1", "added.txt"), "New on the left");
            File.WriteAllText(Path.Combine(src, "notes.txt"), "Changed");
            File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddHours(1));
            File.WriteAllText(Path.Combine(right, "only right.txt"), "Only on the right");

            var result = FolderCompare.Run(src, right, true);
            Expect(result.Count(CompareState.LeftOnly) == 1 && result.Count(CompareState.RightOnly) == 1 && result.Count(CompareState.LeftNewer) == 1,
                "1 left only, 1 right only, 1 left newer; got " + string.Join(", ", result.Entries.Where(e => e.State != CompareState.Same).Select(e => e.State + " " + e.Relative)));
            Expect(result.Count(CompareState.Same) == 3, "3 the same, got " + result.Count(CompareState.Same));

            var changed = result.Entries.Where(e => e.State != CompareState.Same).ToList();
            var job = RunJob(FolderCompare.CopySpec(result, changed, true));
            Expect(job.Record.Result == JobResult.Done && job.Record.Copied == 2, "2 copied to the right, got " + job.Record.Copied + ": " + job.Record.Message);
            Expect(Read(Path.Combine(right, "March", "Week 1", "added.txt")) == "New on the left", "copied into the same subfolder");
            job = RunJob(FolderCompare.CopySpec(result, changed, false));
            Expect(Read(Path.Combine(src, "only right.txt")) == "Only on the right", "copied back to the left");

            var again = FolderCompare.Run(src, right, true);
            Expect(again.Identical, "the folders match afterwards");
        }

        // ================================================================== windows

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            w.Close();
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }
}
