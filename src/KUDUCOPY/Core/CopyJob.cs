using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kuducopy.Core
{
    public enum JobPhase { Starting, Scanning, Waiting, Copying, Resolving, Verifying, Cleaning, Finished }

    public enum FileState { Waiting, Copying, Retrying, Copied, Verified, Skipped, Failed }

    public sealed class FileItem
    {
        public PlannedFile File { get; set; }
        public FileState State { get; set; }
        public double Percent { get; set; }
        public string Reason { get; set; } = string.Empty;

        /// <summary>For "keep both": the new name the copy got.</summary>
        public string FinalDest { get; set; }

        /// <summary>Copied by KUDUCOPY itself (keep both, or a per-file choice) instead of robocopy.</summary>
        public Conflict Manual { get; set; } = Conflict.Ask;
        public bool IsManual => Manual == Conflict.Overwrite || Manual == Conflict.KeepBoth;
    }

    public sealed class ProgressInfo
    {
        public long TotalBytes { get; set; }
        public long DoneBytes { get; set; }
        public int TotalFiles { get; set; }
        public int DoneFiles { get; set; }
        public FileItem Current { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan? Remaining { get; set; }
        public double Fraction => TotalBytes <= 0 ? (TotalFiles <= 0 ? 0 : (double)DoneFiles / TotalFiles) : Math.Min(1, (double)DoneBytes / TotalBytes);
    }

    /// <summary>What the user is asked when files already exist.</summary>
    public sealed class ConflictQuestion
    {
        public List<PlannedFile> Conflicts { get; set; }
        public string Destination { get; set; }
        public int NewerCount => Conflicts.Count(c => c.Existing == Existing.SourceNewer || c.Existing == Existing.Different);
    }

    public sealed class ConflictAnswer
    {
        public Conflict Policy { get; set; }
        public bool Remember { get; set; }
        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Runs one copy or move job with robocopy.exe: lists the files (robocopy /L), asks about existing files, copies with
    /// live progress, copies "keep both" files itself, checks every file arrived, and optionally verifies checksums.
    /// Events are raised on a background thread.
    /// </summary>
    public sealed class CopyJob
    {
        private readonly object _lock = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Dictionary<string, FileItem> _bySource = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(DateTime Time, long Bytes)> _samples = new List<(DateTime, long)>();
        private readonly Dictionary<string, string> _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private StreamWriter _log;
        private Process _process;
        private volatile bool _paused;
        private volatile bool _skipRequested;
        private FileItem _current;
        private long _doneBytes;
        private int _doneFiles;
        private DateTime _lastProgress;
        private string _fatal;
        private Stopwatch _clock = new Stopwatch();

        public CopyJob(JobSpec spec)
        {
            Spec = spec.Clone();
            Record = new JobRecord { Mode = spec.Mode, To = spec.Destination, From = spec.SourceSummary, Spec = spec.Clone() };
        }

        public JobSpec Spec { get; }
        public JobRecord Record { get; }
        public List<FileItem> Items { get; } = new List<FileItem>();
        public JobPhase Phase { get; private set; }
        public string PhaseText { get; private set; } = "Starting…";
        public bool IsPaused => _paused;
        public bool IsCancelled => _cancel.IsCancellationRequested;
        public long TotalBytes { get; private set; }
        public int SkippedExisting { get; private set; }
        public IReadOnlyDictionary<string, string> Hashes => _hashes;
        public string QueueKey => Planner.VolumeKey(Spec.Destination);

        public event Action<JobPhase, string> PhaseChanged;
        public event Action<ProgressInfo> Progress;
        public event Action<FileItem> FileChanged;
        public event Action ItemsReady;

        /// <summary>Asked (on the worker thread) when files already exist and the choice is "Ask me".</summary>
        public Func<ConflictQuestion, ConflictAnswer> AskConflicts { get; set; }

        /// <summary>Asked when the user chose "Let me decide for each file". Returns Overwrite, Skip or KeepBoth per file.</summary>
        public Func<List<PlannedFile>, Dictionary<PlannedFile, Conflict>> DecideEach { get; set; }

        public static string RobocopyPath => Path.Combine(Environment.SystemDirectory, "Robocopy.exe");

        // ================================================================== control

        public void Pause()
        {
            if (_paused) return;
            _paused = true;
            var p = _process;
            try { if (p != null && !p.HasExited) NtSuspendProcess(p.Handle); }
            catch { /* already finished */ }
            SetPhase(Phase, "Paused");
        }

        public void Resume()
        {
            if (!_paused) return;
            _paused = false;
            var p = _process;
            try { if (p != null && !p.HasExited) NtResumeProcess(p.Handle); }
            catch { /* already finished */ }
            SetPhase(Phase, PhaseTextFor(Phase));
        }

        /// <summary>Skips the file being copied now. Robocopy is restarted without it.</summary>
        public void SkipCurrent()
        {
            if (Phase != JobPhase.Copying || _current == null) return;
            _skipRequested = true;
            KillProcess();
        }

        public void Cancel()
        {
            _cancel.Cancel();
            _paused = false;
            KillProcess();
        }

        private void KillProcess()
        {
            var p = _process;
            try
            {
                if (p != null && !p.HasExited)
                {
                    try { NtResumeProcess(p.Handle); } catch { /* not suspended */ }
                    p.Kill();
                }
            }
            catch
            {
                // Already finished.
            }
        }

        // ================================================================== run

        public Task RunAsync() => Task.Run(() => Run());

        /// <summary>Lists what the job would do (robocopy /L), without copying anything or saving History.</summary>
        public Task<PreviewResult> PreviewAsync() => Task.Run(() =>
        {
            var planned = new List<PlannedFile>();
            var extras = new List<PlannedFile>();
            var deletes = Spec.Advanced && (Spec.Switches ?? new List<SwitchValue>())
                .Any(s => s.Switch.Equals("/MIR", StringComparison.OrdinalIgnoreCase) || s.Switch.Equals("/PURGE", StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(RobocopyPath)) throw new InvalidOperationException("Robocopy wasn't found in " + Environment.SystemDirectory + ".");
            foreach (var task in Planner.Tasks(Spec))
            {
                _cancel.Token.ThrowIfCancellationRequested();
                planned.AddRange(Scan(task, deletes ? extras : null));
            }
            return PreviewResult.Build(Spec, planned, extras);
        });

        private void Run()
        {
            Record.Started = DateTime.Now;
            try
            {
                Directory.CreateDirectory(AppPaths.History);
                _log = new StreamWriter(Record.LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                Log($"KUDUCOPY {AppInfo.VersionText} · {Spec.Verb} {Spec.SourceSummary} → {Spec.Destination} · started {Record.Started:yyyy-MM-dd HH:mm:ss}");
                RunPhases();
            }
            catch (OperationCanceledException)
            {
                Record.Result = JobResult.Cancelled;
                Record.Message = "Cancelled";
            }
            catch (Exception ex)
            {
                Record.Result = JobResult.Failed;
                Record.Message = ex.Message;
                Log("ERROR: " + ex);
            }
            finally
            {
                if (IsCancelled && Record.Result != JobResult.Failed)
                {
                    Record.Result = JobResult.Cancelled;
                    Record.Message = "Cancelled";
                }
                Record.Finished = DateTime.Now;
                Summarise();
                Log($"Finished {Record.Finished:yyyy-MM-dd HH:mm:ss}: {Record.Result} · {Record.Copied} copied, {Record.Failed.Count} failed, {Record.Skipped} skipped. {Record.Message}");
                try { _log?.Dispose(); } catch { /* ignore */ }
                try
                {
                    History.Save(Record);
                    History.Trim();
                }
                catch
                {
                    // History is best-effort.
                }
                SetPhase(JobPhase.Finished, Record.Message);
            }
        }

        private void RunPhases()
        {
            // 1. Plan and list the files.
            SetPhase(JobPhase.Scanning, PhaseTextFor(JobPhase.Scanning));
            var tasks = Planner.Tasks(Spec);
            if (!File.Exists(RobocopyPath)) throw new InvalidOperationException("Robocopy wasn't found in " + Environment.SystemDirectory + ".");

            var planned = new List<PlannedFile>();
            foreach (var task in tasks)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                planned.AddRange(Scan(task));
            }
            Log($"Found {planned.Count} files ({Format.Size(planned.Sum(p => p.Size))}).");

            // 2. Existing files.
            var policy = Spec.Advanced ? Conflict.Overwrite : Spec.Conflict;
            var manual = new Dictionary<PlannedFile, Conflict>();
            var conflicts = Spec.Advanced ? new List<PlannedFile>() : planned.Where(p => p.Exists).ToList();

            if (!Spec.Advanced && policy == Conflict.Ask && conflicts.Count > 0)
            {
                var answer = AskConflicts?.Invoke(new ConflictQuestion { Conflicts = conflicts, Destination = Spec.Destination })
                             ?? new ConflictAnswer { Policy = Conflict.Skip };
                if (answer.Cancel) throw new OperationCanceledException();
                policy = answer.Policy;
                if (answer.Remember) Settings.Conflict = policy;
            }
            if (policy == Conflict.Ask) policy = Conflict.Skip; // nothing exists, so any rule copies everything

            if (!Spec.Advanced && policy == Conflict.Decide && conflicts.Count > 0)
            {
                var decided = DecideEach?.Invoke(conflicts);
                if (decided == null) throw new OperationCanceledException();
                foreach (var pair in decided)
                    if (pair.Value == Conflict.Overwrite || pair.Value == Conflict.KeepBoth) manual[pair.Key] = pair.Value;
            }
            else if (!Spec.Advanced && policy == Conflict.KeepBoth)
            {
                foreach (var c in conflicts) manual[c] = Conflict.KeepBoth;
            }

            var robocopyPolicy = policy == Conflict.KeepBoth || policy == Conflict.Decide ? Conflict.Skip : policy;
            Log("Existing files: " + (Spec.Advanced ? "Advanced options decide" : policy.ToString()));

            // 3. The files that will actually be copied.
            foreach (var p in planned)
            {
                if (manual.TryGetValue(p, out var choice))
                {
                    Add(new FileItem { File = p, Manual = choice });
                }
                else if (Spec.Advanced || p.CopiedBy(robocopyPolicy))
                {
                    Add(new FileItem { File = p });
                }
                else
                {
                    SkippedExisting++;
                }
            }
            TotalBytes = Items.Sum(i => i.File.Size);
            ItemsReady?.Invoke();
            Report(true);

            // 4. Wait for other jobs on the same drive.
            using (JobQueue.Enter(this, () => SetPhase(JobPhase.Waiting, PhaseTextFor(JobPhase.Waiting)), _cancel.Token))
            {
                CheckFreeSpace();
                _clock.Start();

                // 5. Robocopy.
                SetPhase(JobPhase.Copying, PhaseTextFor(JobPhase.Copying));
                var moveWithRobocopy = Spec.Mode == CopyMode.Move && !Spec.Verify;
                foreach (var task in tasks)
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    RunRobocopy(task, robocopyPolicy, moveWithRobocopy);
                    if (_fatal != null && !Spec.KeepGoing) break;
                }

                // 6. Files KUDUCOPY copies itself.
                var manualItems = Items.Where(i => i.IsManual).ToList();
                if (manualItems.Count > 0)
                {
                    SetPhase(JobPhase.Resolving, PhaseTextFor(JobPhase.Resolving));
                    foreach (var item in manualItems)
                    {
                        _cancel.Token.ThrowIfCancellationRequested();
                        CopyOurselves(item);
                    }
                }
            }

            // 7. Check every file arrived.
            Confirm(Spec.Mode == CopyMode.Move && !Spec.Verify);

            // 8. Verify, and for moves with verify: remove the originals that checked out.
            if (Spec.Verify) VerifyAll();
            if (Spec.Mode == CopyMode.Move && Spec.Verify) RemoveOriginals(tasks);
        }

        private void Add(FileItem item)
        {
            Items.Add(item);
            _bySource[item.File.Source] = item;
        }

        // ================================================================== scan (robocopy /L)

        private List<PlannedFile> Scan(RoboTask task, List<PlannedFile> extras = null)
        {
            var list = new List<PlannedFile>();
            var srcRoot = task.SourceDir.TrimEnd('\\') + "\\";
            var destRoot = task.DestDir.TrimEnd('\\') + "\\";
            var args = Planner.Build(Spec, task, true, Conflict.Overwrite, false);
            Log("Listing: robocopy " + Args.Join(args));

            var exit = RunProcess(args, line =>
            {
                var l = RoboOutput.Parse(line);
                if (l.Kind != LineKind.File) return;
                if (l.Path.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase) && !l.Path.StartsWith(srcRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // An "extra" file in the destination: only deleted when /PURGE or /MIR is on.
                    extras?.Add(new PlannedFile { Dest = l.Path, Size = l.Size, Task = task });
                    return;
                }
                if (!l.Path.StartsWith(srcRoot, StringComparison.OrdinalIgnoreCase)) return;
                var rel = l.Path.Substring(srcRoot.Length);
                var p = new PlannedFile { Source = l.Path, Dest = Path.Combine(task.DestDir, rel), Size = l.Size, Task = task };
                try
                {
                    var si = new FileInfo(p.Source);
                    if (si.Exists) p.SourceTime = si.LastWriteTimeUtc;
                    var di = new FileInfo(p.Dest);
                    if (di.Exists)
                    {
                        p.DestSize = di.Length;
                        p.DestTime = di.LastWriteTimeUtc;
                        p.Existing = PlannedFile.Compare(p.Size, p.SourceTime, p.DestSize, p.DestTime);
                    }
                }
                catch
                {
                    // Treat unreadable details as a new file; robocopy will report any real problem.
                }
                list.Add(p);
            });

            if (RoboOutput.IsFatal(exit) && !IsCancelled)
                throw new InvalidOperationException($"Robocopy couldn't read \"{task.SourceDir}\" (exit code {exit}). See the log for details.");
            return list;
        }

        // ================================================================== copy (robocopy)

        private bool _userLogStarted;

        private void RunRobocopy(RoboTask task, Conflict policy, bool move)
        {
            var excluded = new List<string>();
            var restarted = false;
            while (true)
            {
                _skipRequested = false;
                var args = Planner.Build(Spec, task, false, policy, move, excluded);
                if (restarted && policy == Conflict.Overwrite)
                {
                    // Files copied before the restart are now identical; without /IS robocopy won't copy them again.
                    args.Remove("/IS");
                }
                Log("Copying: robocopy " + Args.Join(args));

                var output = new StringBuilder();
                var exit = RunProcess(args, line =>
                {
                    if (RoboOutput.Parse(line).Kind != LineKind.Percent) output.AppendLine(line.TrimEnd());
                    OnCopyLine(line);
                });
                if (output.Length > 0)
                {
                    WriteUserLog($"robocopy {Args.Join(args)}\r\n{output}\r\n", !_userLogStarted);
                    _userLogStarted = true;
                }

                if (IsCancelled)
                {
                    // Robocopy was stopped mid-file: that file isn't copied.
                    if (_current != null)
                    {
                        DeletePartial(_current);
                        _current.State = FileState.Waiting;
                        _current.Percent = 0;
                        FileChanged?.Invoke(_current);
                        _current = null;
                    }
                    throw new OperationCanceledException();
                }

                if (_skipRequested)
                {
                    var skipped = _current;
                    if (skipped != null)
                    {
                        skipped.State = FileState.Skipped;
                        skipped.Reason = "Skipped by you";
                        DeletePartial(skipped);
                        FileChanged?.Invoke(skipped);
                        excluded.Add(skipped.File.Source);
                    }
                    _current = null;
                    restarted = true;
                    continue;
                }

                FinishCurrent();
                if (RoboOutput.IsFatal(exit))
                {
                    _fatal = $"Robocopy stopped with exit code {exit} while copying \"{task.SourceDir}\".";
                    Log(_fatal);
                }
                return;
            }
        }

        private void OnCopyLine(string line)
        {
            var l = RoboOutput.Parse(line);
            switch (l.Kind)
            {
                case LineKind.File:
                    if (!_bySource.TryGetValue(l.Path, out var item))
                    {
                        // Not something we planned to copy (e.g. a deleted extra file with /PURGE). Just log it.
                        return;
                    }
                    FinishCurrent();
                    _current = item;
                    item.State = FileState.Copying;
                    item.Percent = 0;
                    FileChanged?.Invoke(item);
                    Report();
                    break;

                case LineKind.Percent:
                    if (_current == null) return;
                    _current.Percent = l.Percent;
                    if (_current.State == FileState.Retrying)
                    {
                        _current.State = FileState.Copying;
                        _current.Reason = string.Empty;
                        FileChanged?.Invoke(_current);
                    }
                    Report();
                    break;

                case LineKind.Error:
                    _errors[l.Path] = l.ErrorCode;
                    if (_bySource.TryGetValue(l.Path, out var failed))
                    {
                        failed.State = FileState.Retrying;
                        failed.Reason = RoboOutput.Reason(l.ErrorCode) + (Spec.KeepGoing ? " · retrying" : string.Empty);
                        FileChanged?.Invoke(failed);
                    }
                    if (!Spec.KeepGoing)
                    {
                        _fatal = "Stopped at the first error, because \"Keep going if a file can't be copied\" is off.";
                        KillProcess();
                    }
                    break;
            }
        }

        private void FinishCurrent()
        {
            var item = _current;
            if (item == null) return;
            _current = null;
            if (item.State == FileState.Copying)
            {
                // Confirmed later by checking the destination; counted now for progress.
                item.Percent = 100;
                Interlocked.Add(ref _doneBytes, item.File.Size);
                Interlocked.Increment(ref _doneFiles);
                item.State = FileState.Copied;
                FileChanged?.Invoke(item);
            }
            Report();
        }

        private void DeletePartial(FileItem item)
        {
            // Only remove a half-copied file that didn't exist before; never touch a file the user already had.
            if (item.File.Exists) return;
            try { if (File.Exists(item.File.Dest)) File.Delete(item.File.Dest); }
            catch { /* leave it */ }
        }

        /// <summary>
        /// Runs robocopy and feeds its output line by line. The output is read from a Unicode log file (/UNILOG) while
        /// robocopy writes it: robocopy's console output replaces characters outside the console code page with "?".
        /// </summary>
        private int RunProcess(List<string> args, Action<string> onLine)
        {
            var logFile = Path.Combine(AppPaths.Temp, "robocopy-" + Guid.NewGuid().ToString("N") + ".log");
            var allArgs = new List<string>(args) { "/UNILOG:" + logFile };
            var psi = new ProcessStartInfo(RobocopyPath, Args.Join(allArgs))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                    p.Start();
                    _process = p;
                    if (_paused) { try { NtSuspendProcess(p.Handle); } catch { /* ignore */ } }
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (_cancel.Token.Register(KillProcess))
                    {
                        TailLog(logFile, p, onLine);
                        p.WaitForExit();
                    }
                    _process = null;
                    return p.ExitCode;
                }
            }
            finally
            {
                TryDelete(logFile);
            }
        }

        /// <summary>Reads a UTF-16 log file while it grows, until the process has exited and everything is read.</summary>
        private void TailLog(string path, Process p, Action<string> onLine)
        {
            var decoder = Encoding.Unicode.GetDecoder();
            var bytes = new byte[64 * 1024];
            var chars = new char[bytes.Length];
            var pending = new StringBuilder();
            FileStream stream = null;
            try
            {
                while (true)
                {
                    var exited = p.HasExited;
                    if (stream == null && File.Exists(path))
                    {
                        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                        catch (IOException) { /* robocopy is creating it; try again */ }
                    }

                    var read = 0;
                    if (stream != null)
                    {
                        read = stream.Read(bytes, 0, bytes.Length);
                        if (read > 0)
                        {
                            var n = decoder.GetChars(bytes, 0, read, chars, 0);
                            for (var i = 0; i < n; i++)
                            {
                                var c = chars[i];
                                if (c == '\r' || c == '\n')
                                {
                                    if (pending.Length > 0) Line(pending.ToString(), onLine);
                                    pending.Clear();
                                }
                                else
                                {
                                    pending.Append(c);
                                }
                            }
                        }
                    }

                    if (read == 0)
                    {
                        if (exited) break; // the process had already ended before this read, so nothing more is coming
                        Thread.Sleep(60);
                    }
                }
                if (pending.Length > 0) Line(pending.ToString(), onLine);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>Advanced mode: the user's own /LOG, /LOG+, /UNILOG or /UNILOG+ file, written from KUDUCOPY's log.</summary>
        private void WriteUserLog(string text, bool first)
        {
            foreach (var sv in (Spec.Advanced ? Spec.Switches : new List<SwitchValue>()).Where(s => RoboOptions.IsLogOption(s.Switch)))
            {
                var path = (sv.Value ?? string.Empty).Trim().Trim('"');
                if (path.Length == 0) continue;
                try
                {
                    var append = sv.Switch.EndsWith("+", StringComparison.Ordinal) || !first;
                    var unicode = sv.Switch.StartsWith("/UNILOG", StringComparison.OrdinalIgnoreCase);
                    var encoding = unicode ? (Encoding)new UnicodeEncoding(false, true) : Encoding.Default;
                    if (append) File.AppendAllText(path, text, encoding);
                    else File.WriteAllText(path, text, encoding);
                }
                catch (Exception ex)
                {
                    Log("Couldn't write the log file " + path + ": " + ex.Message);
                }
            }
        }

        private void Line(string data, Action<string> onLine)
        {
            if (data == null) return;
            var line = data.Replace("﻿", string.Empty).Replace("\0", string.Empty);
            if (line.Trim().Length == 0) return;
            if (RoboOutput.Parse(line).Kind != LineKind.Percent) Log(line.TrimEnd());
            try { onLine(line); }
            catch (Exception ex) { Log("Couldn't read output line: " + ex.Message); }
        }

        // ================================================================== copy (KUDUCOPY itself)

        private void CopyOurselves(FileItem item)
        {
            var target = item.Manual == Conflict.KeepBoth ? PlannedFile.KeepBothName(item.File.Dest, File.Exists) : item.File.Dest;
            item.FinalDest = target;
            item.State = FileState.Copying;
            _current = item;
            FileChanged?.Invoke(item);
            Log($"Copying ({(item.Manual == Conflict.KeepBoth ? "keep both" : "replace")}): {item.File.Source} → {target}");

            var temp = target + ".kuducopy-part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
                using (var input = new FileStream(item.File.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    long copied = 0;
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        while (_paused) Thread.Sleep(150);
                        _cancel.Token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                        copied += n;
                        item.Percent = item.File.Size == 0 ? 100 : 100.0 * copied / item.File.Size;
                        Report();
                    }
                }

                var info = new FileInfo(item.File.Source);
                File.SetCreationTimeUtc(temp, info.CreationTimeUtc);
                File.SetLastWriteTimeUtc(temp, info.LastWriteTimeUtc);
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                File.Move(temp, target);
                File.SetAttributes(target, info.Attributes);

                item.State = FileState.Copied;
                Interlocked.Add(ref _doneBytes, item.File.Size);
                Interlocked.Increment(ref _doneFiles);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                item.State = FileState.Failed;
                item.Reason = Friendly(ex);
                Log($"Failed: {item.File.Source}: {ex.Message}");
            }
            _current = null;
            FileChanged?.Invoke(item);
            Report();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* ignore */ }
        }

        private static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Access is denied";
            if (ex is IOException io)
            {
                var code = io.HResult & 0xFFFF;
                if (code == 32 || code == 33 || code == 112) return RoboOutput.Reason(code);
            }
            return ex.Message.TrimEnd('.');
        }

        // ================================================================== confirm, verify, clean up

        private void Confirm(bool movedByRobocopy)
        {
            foreach (var item in Items)
            {
                if (item.State == FileState.Skipped || item.State == FileState.Failed) continue;
                var dest = item.FinalDest ?? item.File.Dest;
                bool ok;
                try
                {
                    var di = new FileInfo(dest);
                    ok = di.Exists && di.Length == item.File.Size;
                }
                catch
                {
                    ok = false;
                }

                if (!ok)
                {
                    item.State = FileState.Failed;
                    if (!_errors.TryGetValue(item.File.Source, out var code)) _errors.TryGetValue(item.File.Dest, out code);
                    item.Reason = code != 0 ? RoboOutput.Reason(code) : _fatal != null ? "Not copied: the job stopped early" : "Not copied";
                }
                else
                {
                    item.State = FileState.Copied;
                    item.Percent = 100;
                    if (movedByRobocopy && File.Exists(item.File.Source))
                    {
                        item.State = FileState.Failed;
                        item.Reason = "Copied, but the original couldn't be removed";
                    }
                    else if (Spec.Mode == CopyMode.Move && item.IsManual && !Spec.Verify)
                    {
                        RemoveSource(item);
                    }
                }
                FileChanged?.Invoke(item);
            }
            Report(true);
        }

        private void VerifyAll()
        {
            var toVerify = Items.Where(i => i.State == FileState.Copied).ToList();
            if (toVerify.Count == 0) return;
            SetPhase(JobPhase.Verifying, PhaseTextFor(JobPhase.Verifying));
            Interlocked.Exchange(ref _doneBytes, 0);
            Interlocked.Exchange(ref _doneFiles, 0);
            TotalBytes = toVerify.Sum(i => i.File.Size) * 2;
            lock (_samples) _samples.Clear();

            foreach (var item in toVerify)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                _current = item;
                FileChanged?.Invoke(item);
                var dest = item.FinalDest ?? item.File.Dest;
                try
                {
                    var a = Checksums.HashFile(item.File.Source, Spec.Hash, n => { Interlocked.Add(ref _doneBytes, n); Report(); }, _cancel.Token, () => _paused);
                    var b = Checksums.HashFile(dest, Spec.Hash, n => { Interlocked.Add(ref _doneBytes, n); Report(); }, _cancel.Token, () => _paused);
                    if (a == b)
                    {
                        item.State = FileState.Verified;
                        _hashes[dest] = b;
                    }
                    else
                    {
                        item.State = FileState.Failed;
                        item.Reason = "The copy doesn't match the original (" + Checksums.Name(Spec.Hash) + " differs)";
                        Log($"Verify failed: {item.File.Source} {a} ≠ {dest} {b}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    item.State = FileState.Failed;
                    item.Reason = "Couldn't verify: " + Friendly(ex);
                }
                Interlocked.Increment(ref _doneFiles);
                _current = null;
                FileChanged?.Invoke(item);
                Report();
            }
        }

        private void RemoveOriginals(List<RoboTask> tasks)
        {
            SetPhase(JobPhase.Cleaning, PhaseTextFor(JobPhase.Cleaning));
            foreach (var item in Items.Where(i => i.State == FileState.Verified)) RemoveSource(item);

            // Remove source folders that are now empty (deepest first), like a move would.
            foreach (var task in tasks.Where(t => t.IsFolder))
            {
                try
                {
                    var dirs = Directory.GetDirectories(task.SourceDir, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length).ToList();
                    dirs.Add(task.SourceDir);
                    foreach (var d in dirs)
                    {
                        try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                        catch { /* keep it */ }
                    }
                }
                catch
                {
                    // Folder already gone or unreadable.
                }
            }
        }

        private void RemoveSource(FileItem item)
        {
            try
            {
                File.SetAttributes(item.File.Source, FileAttributes.Normal);
                File.Delete(item.File.Source);
            }
            catch (Exception ex)
            {
                item.State = FileState.Failed;
                item.Reason = "Copied, but the original couldn't be removed: " + Friendly(ex);
                FileChanged?.Invoke(item);
            }
        }

        private void CheckFreeSpace()
        {
            try
            {
                var root = Path.GetPathRoot(Planner.Full(Spec.Destination));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) throw new InvalidOperationException($"The drive {root} isn't ready. Is it plugged in?");
                // Files that replace existing ones only need the difference.
                var needed = Items.Sum(i => i.Manual == Conflict.KeepBoth || !i.File.Exists ? i.File.Size : Math.Max(0, i.File.Size - i.File.DestSize));
                if (needed > drive.AvailableFreeSpace)
                    throw new InvalidOperationException($"Not enough space on {root}: this needs {Format.Size(needed)}, but only {Format.Size(drive.AvailableFreeSpace)} is free.");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // Can't tell (e.g. some network drives): let robocopy report it if it runs out.
            }
        }

        private void Summarise()
        {
            Record.Files = Items.Count;
            Record.Bytes = Items.Sum(i => i.File.Size);
            Record.Copied = Items.Count(i => i.State == FileState.Copied || i.State == FileState.Verified);
            Record.Skipped = SkippedExisting + Items.Count(i => i.State == FileState.Skipped);
            Record.Verified = Spec.Verify && Items.Count > 0 && Items.All(i => i.State == FileState.Verified || i.State == FileState.Skipped);
            Record.Failed = Items.Where(i => i.State == FileState.Failed)
                .Select(i => new FailedFile { Source = i.File.Source, Dest = i.FinalDest ?? i.File.Dest, Reason = i.Reason })
                .ToList();

            if (Record.Result == JobResult.Cancelled || Record.Result == JobResult.Failed) return;
            if (_fatal != null && Record.Copied == 0 && Items.Count > 0)
            {
                Record.Result = JobResult.Failed;
                Record.Message = _fatal;
            }
            else if (Record.Failed.Count > 0 || _fatal != null)
            {
                Record.Result = JobResult.DoneWithProblems;
                Record.Message = _fatal ?? (Record.Failed.Count == 1 ? "1 file couldn't be copied." : Record.Failed.Count + " files couldn't be copied.");
            }
            else
            {
                Record.Result = JobResult.Done;
                Record.Message = Items.Count == 0
                    ? (SkippedExisting > 0 ? "Nothing to copy: every file was already there." : "Nothing to copy.")
                    : "Done";
            }
        }

        // ================================================================== progress

        private void SetPhase(JobPhase phase, string text)
        {
            Phase = phase;
            PhaseText = text;
            PhaseChanged?.Invoke(phase, text);
        }

        private string PhaseTextFor(JobPhase phase)
        {
            switch (phase)
            {
                case JobPhase.Scanning: return "Finding files…";
                case JobPhase.Waiting: return "Waiting for another job on " + QueueKey + " to finish…";
                case JobPhase.Copying: return Spec.Verbing + "…";
                case JobPhase.Resolving: return Spec.Verbing + " files you chose to keep or replace…";
                case JobPhase.Verifying: return "Verifying copies (" + Checksums.Name(Spec.Hash) + ")…";
                case JobPhase.Cleaning: return "Removing the originals…";
                default: return string.Empty;
            }
        }

        private void Report(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastProgress).TotalMilliseconds < 120) return;
            _lastProgress = now;

            var current = _current;
            var done = Interlocked.Read(ref _doneBytes);
            if (current != null && Phase != JobPhase.Verifying) done += (long)(current.File.Size * current.Percent / 100.0);
            done = Math.Min(done, TotalBytes);

            double speed = 0;
            lock (_samples)
            {
                if (!_paused) _samples.Add((now, done));
                _samples.RemoveAll(s => (now - s.Time).TotalSeconds > 5);
                if (_samples.Count >= 2)
                {
                    var first = _samples[0];
                    var seconds = (now - first.Time).TotalSeconds;
                    if (seconds > 0.5) speed = (done - first.Bytes) / seconds;
                }
            }

            Progress?.Invoke(new ProgressInfo
            {
                TotalBytes = TotalBytes,
                DoneBytes = done,
                TotalFiles = Items.Count,
                DoneFiles = _doneFiles,
                Current = current,
                BytesPerSecond = speed,
                Remaining = speed > 0 && TotalBytes > done ? TimeSpan.FromSeconds((TotalBytes - done) / speed) : (TimeSpan?)null
            });
        }

        private void Log(string text)
        {
            try { _log?.WriteLine(text); }
            catch { /* ignore */ }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);
    }

    /// <summary>Jobs that copy to the same drive run one after another; jobs to different drives run at the same time.</summary>
    public static class JobQueue
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Queue<CopyJob>> Waiting = new Dictionary<string, Queue<CopyJob>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CopyJob> Running = new Dictionary<string, CopyJob>(StringComparer.OrdinalIgnoreCase);

        public static event Action Changed;

        public static int CountRunning
        {
            get { lock (Lock) return Running.Count; }
        }

        public static int CountWaiting
        {
            get { lock (Lock) return Waiting.Values.Sum(q => q.Count); }
        }

        public static bool IsBusy(string key)
        {
            lock (Lock) return Running.ContainsKey(key);
        }

        public static IDisposable Enter(CopyJob job, Action onWait, CancellationToken cancel)
        {
            var key = job.QueueKey;
            var waited = false;
            lock (Lock)
            {
                if (!Waiting.TryGetValue(key, out var q)) Waiting[key] = q = new Queue<CopyJob>();
                q.Enqueue(job);
            }
            Changed?.Invoke();
            try
            {
                while (true)
                {
                    lock (Lock)
                    {
                        var q = Waiting[key];
                        if (!Running.ContainsKey(key) && q.Peek() == job)
                        {
                            q.Dequeue();
                            Running[key] = job;
                            break;
                        }
                    }
                    if (!waited)
                    {
                        waited = true;
                        onWait();
                    }
                    if (cancel.WaitHandle.WaitOne(250)) cancel.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                lock (Lock)
                {
                    var q = Waiting[key];
                    Waiting[key] = new Queue<CopyJob>(q.Where(j => j != job));
                }
                Changed?.Invoke();
                throw;
            }
            Changed?.Invoke();
            return new Release(key);
        }

        private sealed class Release : IDisposable
        {
            private readonly string _key;
            private bool _done;
            public Release(string key) => _key = key;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                lock (Lock) Running.Remove(_key);
                Changed?.Invoke();
            }
        }
    }
}
