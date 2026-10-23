using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kuducopy.Core;
using Kuducopy.Views;

namespace Kuducopy
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var cmd = CommandLine.Parse(args);

            // Installer hooks: no window.
            if (cmd.Action == Launch.RegisterShell)
            {
                Settings.ShellMenu = true;
                ShellIntegration.Register(Settings.Favourites);
                return 0;
            }
            if (cmd.Action == Launch.Unregister)
            {
                ShellIntegration.RemoveAll();
                return 0;
            }

            if (cmd.Action == Launch.SelfTest) return new App().RunSelfTest();

            // One KUDUCOPY per user: later starts hand their command over and exit.
            App app = null;
            var hub = Hub.Start(args, received => app?.Dispatcher.BeginInvoke(new Action(() => app.Handle(CommandLine.Parse(received)))));
            if (hub == null) return 0;
            using (hub)
            {
                app = new App();
                return app.Run(cmd);
            }
        }
    }

    public sealed class App : Application
    {
        private MainWindow _main;
        private readonly Dictionary<string, CommandLine> _batches = new Dictionary<string, CommandLine>();
        private readonly Dictionary<string, DispatcherTimer> _batchTimers = new Dictionary<string, DispatcherTimer>();

        public static App Instance => (App)Current;

        public int Run(CommandLine cmd)
        {
            Startup += (s, e) =>
            {
                Init();
                ShellIntegration.Refresh();
                Handle(cmd);
            };
            return Run();
        }

        public int RunSelfTest()
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run();
                Shutdown(code);
            };
            Run();
            return code;
        }

        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        /// <summary>Acts on a command from this start or from a later start (right-click menus).</summary>
        internal void Handle(CommandLine cmd)
        {
            switch (cmd.Action)
            {
                case Launch.Paste:
                    var files = ClipboardFiles.Get(out var cut);
                    if (files.Count == 0)
                    {
                        Dialogs.Info(null, "There's nothing to paste. Copy (Ctrl+C) or cut (Ctrl+X) files in File Explorer first.");
                        ShutdownIfIdle();
                        return;
                    }
                    var spec = DefaultSpec(files, cmd.Folder, cut ? CopyMode.Move : CopyMode.Copy);
                    if (StartJob(spec, null) && cut)
                    {
                        // Like File Explorer: cut files can only be pasted once.
                        try { Clipboard.Clear(); } catch { /* clipboard busy */ }
                    }
                    break;

                case Launch.CopyTo:
                case Launch.MoveTo:
                case Launch.CopyToAsk:
                case Launch.MoveToAsk:
                    Batch(cmd);
                    break;

                case Launch.Job:
                    var saved = Json.Read<JobSpec>(cmd.Folder);
                    try { File.Delete(cmd.Folder); } catch { /* ignore */ }
                    if (saved != null) StartJob(saved, null);
                    else ShowMain();
                    break;

                default:
                    var main = ShowMain();
                    main.AddSources(cmd.Paths);
                    break;
            }
        }

        /// <summary>Right-clicking many files starts KUDUCOPY once per file; jobs arriving within a moment are merged.</summary>
        private void Batch(CommandLine cmd)
        {
            var key = cmd.BatchKey;
            if (_batches.TryGetValue(key, out var existing))
            {
                existing.Merge(cmd);
                _batchTimers[key].Stop();
                _batchTimers[key].Start();
                return;
            }

            _batches[key] = cmd;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _batchTimers[key] = timer;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _batches.Remove(key);
                _batchTimers.Remove(key);
                var batch = cmd;
                if (batch.Action == Launch.CopyToAsk || batch.Action == Launch.MoveToAsk)
                {
                    var main = ShowMain();
                    main.AddSources(batch.Paths);
                    main.SetMode(batch.Action == Launch.MoveToAsk ? CopyMode.Move : CopyMode.Copy);
                    main.FocusDestination();
                }
                else
                {
                    StartJob(DefaultSpec(batch.Paths, batch.Folder, batch.Action == Launch.MoveTo ? CopyMode.Move : CopyMode.Copy), null);
                }
            };
            timer.Start();
        }

        private static JobSpec DefaultSpec(IEnumerable<string> sources, string destination, CopyMode mode) => new JobSpec
        {
            Sources = sources.ToList(),
            Destination = destination,
            Mode = mode,
            Conflict = Settings.Conflict == Conflict.Decide ? Conflict.Ask : Settings.Conflict,
            Verify = Settings.Verify,
            Hash = Settings.Hash,
            KeepGoing = Settings.KeepGoing,
            Advanced = Settings.Advanced,
            Switches = Settings.Switches,
            ExtraOptions = Settings.ExtraOptions
        };

        internal MainWindow ShowMain()
        {
            if (_main == null || !_main.IsLoaded && !_main.IsVisible)
            {
                _main = new MainWindow();
                _main.Closed += (s, e) => _main = null;
                MainWindow = _main;
                _main.Show();
            }
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            return _main;
        }

        /// <summary>Starts a job in its own window. Returns false if it couldn't start.</summary>
        public static bool StartJob(JobSpec spec, Window owner)
        {
            try
            {
                Planner.Tasks(spec);
                Directory.CreateDirectory(Planner.Full(spec.Destination));
            }
            catch (Exception ex)
            {
                Dialogs.Error(owner, ex is InvalidOperationException ? ex.Message : "That destination can't be used:\n\n" + ex.Message);
                Instance?.ShutdownIfIdle();
                return false;
            }
            var window = new JobWindow(new CopyJob(spec), owner);
            if (Instance != null && Instance.MainWindow == null) Instance.MainWindow = window;
            window.Show();
            window.Activate();
            return true;
        }

        public static void JobFinished(JobWindow window)
        {
            // Nothing to do yet: the window stays open with the result until the user closes it.
        }

        private void ShutdownIfIdle()
        {
            if (Windows.Count == 0 && _batches.Count == 0) Shutdown();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
