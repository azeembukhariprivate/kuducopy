using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Windows.Threading;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    /// <summary>One row in the file list. Updated from the job's events, a few times a second.</summary>
    public sealed class FileRow : INotifyPropertyChanged
    {
        public FileRow(FileItem item, string root)
        {
            Item = item;
            Name = Path.GetFileName(item.File.Source);
            var folder = Path.GetDirectoryName(item.File.Source) ?? string.Empty;
            Folder = folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? folder.Substring(root.Length).TrimStart('\\') : folder;
            if (Folder.Length == 0) Folder = Path.GetFileName(folder.TrimEnd('\\'));
            SizeText = Format.Size(item.File.Size);
            Update();
        }

        public FileItem Item { get; }
        public string Name { get; }
        public string Folder { get; }
        public string SizeText { get; }
        public string Mark { get; private set; }
        public string Tone { get; private set; }
        public string Status { get; private set; }
        public double Percent { get; private set; }
        public bool Busy { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public void Update()
        {
            var i = Item;
            switch (i.State)
            {
                case FileState.Copying: Mark = "●"; Tone = "now"; Status = $"{i.Percent:0}%"; break;
                case FileState.Retrying: Mark = "!"; Tone = "warn"; Status = i.Reason; break;
                case FileState.Copied: Mark = "✓"; Tone = "ok"; Status = i.FinalDest != null ? "Copied as " + Path.GetFileName(i.FinalDest) : "Copied"; break;
                case FileState.Verified: Mark = "✓"; Tone = "ok"; Status = "Copied · verified"; break;
                case FileState.Skipped: Mark = "–"; Tone = "wait"; Status = string.IsNullOrEmpty(i.Reason) ? "Skipped" : i.Reason; break;
                case FileState.Failed: Mark = "✕"; Tone = "bad"; Status = i.Reason; break;
                default: Mark = "○"; Tone = "wait"; Status = i.IsManual ? (i.Manual == Conflict.KeepBoth ? "Waiting · keep both" : "Waiting · replace") : "Waiting"; break;
            }
            Percent = i.Percent;
            Busy = i.State == FileState.Copying;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>Progress of one job, then its result (failed files, retry, checksums).</summary>
    public sealed class JobWindow : Window
    {
        private const string RowTemplate = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Height='30'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='34'/><ColumnDefinition Width='*'/><ColumnDefinition Width='0.8*'/><ColumnDefinition Width='86'/><ColumnDefinition Width='1.1*'/>
    </Grid.ColumnDefinitions>
    <TextBlock x:Name='Mark' Text='{Binding Mark}' FontWeight='Bold' HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{DynamicResource SubtleText}'/>
    <TextBlock Grid.Column='1' Text='{Binding Name}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,8,0' FontWeight='Medium'/>
    <TextBlock Grid.Column='2' Text='{Binding Folder}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' Margin='0,0,8,0'/>
    <TextBlock Grid.Column='3' Text='{Binding SizeText}' VerticalAlignment='Center' HorizontalAlignment='Right' Foreground='{DynamicResource SubtleText}' Margin='0,0,14,0'/>
    <StackPanel Grid.Column='4' Orientation='Horizontal' VerticalAlignment='Center'>
      <ProgressBar x:Name='Bar' Width='90' Height='4' Maximum='100' Value='{Binding Percent, Mode=OneWay}' Margin='0,0,8,0' Visibility='Collapsed'/>
      <TextBlock x:Name='Status' Text='{Binding Status}' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}'/>
    </StackPanel>
  </Grid>
  <DataTemplate.Triggers>
    <DataTrigger Binding='{Binding Tone}' Value='ok'>
      <Setter TargetName='Mark' Property='Foreground' Value='{DynamicResource OkText}'/>
      <Setter TargetName='Status' Property='Foreground' Value='{DynamicResource OkText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='now'>
      <Setter TargetName='Mark' Property='Foreground' Value='{DynamicResource LinkText}'/>
      <Setter TargetName='Status' Property='Foreground' Value='{DynamicResource LinkText}'/>
      <Setter TargetName='Bar' Property='Visibility' Value='Visible'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='warn'>
      <Setter TargetName='Mark' Property='Foreground' Value='{DynamicResource WarnText}'/>
      <Setter TargetName='Status' Property='Foreground' Value='{DynamicResource WarnText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='bad'>
      <Setter TargetName='Mark' Property='Foreground' Value='{DynamicResource BadText}'/>
      <Setter TargetName='Status' Property='Foreground' Value='{DynamicResource BadText}'/>
    </DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>";

        private readonly CopyJob _job;
        private readonly ObservableCollection<FileRow> _rows = new ObservableCollection<FileRow>();
        private readonly Dictionary<FileItem, FileRow> _rowFor = new Dictionary<FileItem, FileRow>();
        private readonly ConcurrentDictionary<FileItem, bool> _changed = new ConcurrentDictionary<FileItem, bool>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private ProgressInfo _progress;
        private JobPhase _phase;
        private string _phaseText = string.Empty;
        private bool _itemsReady;
        private bool _finished;

        // Progress view
        private readonly TextBlock _title = Ui.Text(string.Empty, 16, bold: true);
        private readonly TextBlock _subtitle = Ui.Subtle(string.Empty, 12.5, false);
        private readonly Border _chipHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _percent = Ui.Text("0%", 34, bold: true);
        private readonly TextBlock _amount = Ui.Subtle(string.Empty, 12.5, false);
        private readonly TextBlock _speed = Ui.Text(string.Empty, 15, bold: true);
        private readonly TextBlock _left = Ui.Subtle(string.Empty, 12, false);
        private readonly ProgressBar _bar = new ProgressBar { Height = 10, Maximum = 1 };
        private readonly TextBlock _nowName = Ui.Text(string.Empty, 12.5);
        private readonly TextBlock _nowAmount = Ui.Subtle(string.Empty, 12.5, false);
        private readonly ProgressBar _nowBar = new ProgressBar { Height = 4, Maximum = 100 };
        private readonly Button _pause;
        private readonly Button _skip;
        private readonly Button _cancel;
        private readonly ComboBox _after = new ComboBox { Width = 140, MinHeight = 34 };
        private readonly WrapPanel _counts = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        private readonly ListBox _list = new ListBox().Styled("TableList");
        private readonly Border _listCard;
        private readonly Button _toggleList;
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _statusRight = Ui.Subtle(string.Empty, 12, false);
        private readonly Grid _content = new Grid();
        private UIElement _progressView;

        public JobWindow(CopyJob job, Window owner = null)
        {
            _job = job;
            Width = 900;
            Height = 720;
            MinWidth = 720;
            MinHeight = 420;
            Dialogs.Prepare(this, job.Spec.Verbing + " · KUDUCOPY");
            if (owner != null && owner.IsVisible) WindowStartupLocation = WindowStartupLocation.CenterOwner;
            TaskbarItemInfo = new TaskbarItemInfo { ProgressState = TaskbarItemProgressState.Indeterminate };

            _pause = Ui.Button("Pause", "IconPause", click: (s, e) => TogglePause());
            _skip = Ui.Button("Skip this file", "IconSkip", click: (s, e) => _job.SkipCurrent());
            _cancel = Ui.Button("Cancel", "IconClose", click: (s, e) => CancelJob());
            _cancel.Res(ForegroundProperty, "BadText");
            _toggleList = Ui.Link(Settings.ShowFileList ? "Hide list ▴" : "Show list ▾", (s, e) => ToggleList());

            foreach (AfterFinish a in Enum.GetValues(typeof(AfterFinish)))
                _after.Items.Add(new ComboBoxItem { Content = AfterText(a), Tag = a });
            _after.SelectedIndex = (int)job.Spec.After;
            _after.SelectionChanged += (s, e) => _job.Spec.After = (AfterFinish)((ComboBoxItem)_after.SelectedItem).Tag;

            _list.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate);
            _list.ItemsSource = _rows;
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);

            _listCard = BuildListCard();
            _progressView = BuildProgressView();

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(_content.At(0));
            var sg = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            sg.Children.Add(Ui.Dot("LinkText").Margin(0, 0, 10, 0).At(0, 0));
            sg.Children.Add(_status.At(0, 1));
            sg.Children.Add(_statusRight.At(0, 2));
            root.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 7, 16, 7), Child = sg }
                .Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceAltBg").At(1));
            Content = root;
            _content.Children.Add(_progressView);

            _title.Text = $"{job.Spec.Verbing} {job.Spec.SourceSummary} to {job.Spec.Destination}";
            _subtitle.Text = "From " + string.Join(", ", (job.Spec.Tasks != null && job.Spec.Tasks.Count > 0
                ? job.Spec.Tasks.Select(t => t.SourceDir)
                : job.Spec.Sources.Select(p => Path.GetDirectoryName(p) ?? p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2));
            _status.Text = "Starting…";
            _statusRight.Text = job.Spec.Advanced ? "Advanced options" : string.Empty;

            _job.PhaseChanged += (phase, text) =>
            {
                _phase = phase;
                _phaseText = text;
            };
            _job.Progress += p => _progress = p;
            _job.FileChanged += item => _changed[item] = true;
            _job.ItemsReady += () => _itemsReady = true;
            _job.AskConflicts = q => (ConflictAnswer)Dispatcher.Invoke(new Func<ConflictAnswer>(() => ConflictWindows.Ask(this, q)));
            _job.DecideEach = list => (Dictionary<PlannedFile, Conflict>)Dispatcher.Invoke(new Func<Dictionary<PlannedFile, Conflict>>(() => ConflictWindows.Decide(this, list)));

            _timer.Tick += (s, e) => Tick();
            Loaded += (s, e) =>
            {
                _timer.Start();
                var task = _job.RunAsync();
                task.ContinueWith(t => Dispatcher.BeginInvoke(new Action(Finished)));
            };
            Closing += OnClosing;
        }

        public CopyJob Job => _job;
        public bool IsDone => _finished;

        private static string AfterText(AfterFinish a)
        {
            switch (a)
            {
                case AfterFinish.Close: return "Close";
                case AfterFinish.Sound: return "Play a sound";
                case AfterFinish.Sleep: return "Sleep";
                case AfterFinish.Shutdown: return "Shut down";
                default: return "Do nothing";
            }
        }

        // ================================================================== layout

        private UIElement BuildProgressView()
        {
            var head = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            head.Children.Add(new LogoMark(40).Res(TextElement.ForegroundProperty, "Accent").Margin(0, 0, 14, 0).At(0, 0));
            head.Children.Add(Ui.Column(_title, _subtitle).At(0, 1));
            head.Children.Add(_chipHost.Margin(12, 0, 0, 0).At(0, 2));
            _title.TextTrimming = TextTrimming.CharacterEllipsis;

            var numbers = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            numbers.Children.Add(_percent.At(0, 0));
            _amount.VerticalAlignment = VerticalAlignment.Bottom;
            numbers.Children.Add(_amount.Margin(16, 0, 0, 4).At(0, 1));
            var speed = Ui.Column(_speed, _left);
            _speed.HorizontalAlignment = HorizontalAlignment.Right;
            _left.HorizontalAlignment = HorizontalAlignment.Right;
            numbers.Children.Add(speed.At(0, 2));

            var now = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star());
            now.Children.Add(Ui.Subtle("Now:", 12.5, false).Margin(0, 0, 8, 0).At(0, 0));
            now.Children.Add(_nowName.At(0, 1));
            _nowName.MaxWidth = 380;
            now.Children.Add(_nowAmount.Margin(10, 0, 10, 0).At(0, 2));
            now.Children.Add(_nowBar.At(0, 3));

            var buttons = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            buttons.Children.Add(_pause.At(0, 0));
            buttons.Children.Add(_skip.Margin(8, 0, 0, 0).At(0, 1));
            buttons.Children.Add(_cancel.Margin(8, 0, 0, 0).At(0, 2));
            buttons.Children.Add(Ui.Subtle("When finished:", 12.5, false).Margin(0, 0, 8, 0).At(0, 4));
            buttons.Children.Add(_after.At(0, 5));

            var top = Ui.Column(head, numbers.Margin(0, 18, 0, 0), _bar.Margin(0, 10, 0, 0), now.Margin(0, 12, 0, 0), buttons.Margin(0, 16, 0, 0));
            var grid = new Grid { Margin = new Thickness(22, 18, 22, 16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(top.At(0));
            grid.Children.Add(_listCard.Margin(0, 16, 0, 0).At(1));
            return grid;
        }

        private Border BuildListCard()
        {
            var head = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.Heading("Files", 13).At(0, 0));
            head.Children.Add(_counts.Margin(12, 0, 0, 0).At(0, 1));
            head.Children.Add(_toggleList.At(0, 2));

            var cols = Ui.Columns(Ui.Px(34), Ui.Star(), Ui.Star(0.8), Ui.Px(86), Ui.Star(1.1));
            var titles = new[] { "", "Name", "Folder", "Size", "Status" };
            for (var i = 0; i < titles.Length; i++)
            {
                var t = Ui.Text(titles[i], 12, "SubtleText", bold: true).Margin(0, 7, i == 3 ? 14 : 8, 7).At(0, i);
                if (i == 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                cols.Children.Add(t);
            }

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(head.Margin(14, 10, 14, 10).At(0));
            grid.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 1), Child = cols }.Res(Border.BorderBrushProperty, "BorderBrush").At(1));
            grid.Children.Add(_list.At(2));
            var card = Ui.Card(grid, 0);
            card.ClipToBounds = true;
            if (!Settings.ShowFileList) HideListBody(card, true);
            return card;
        }

        private void ToggleList()
        {
            Settings.ShowFileList = !Settings.ShowFileList;
            _toggleList.Content = Settings.ShowFileList ? "Hide list ▴" : "Show list ▾";
            HideListBody(_listCard, !Settings.ShowFileList);
        }

        private static void HideListBody(Border card, bool hide)
        {
            var grid = (Grid)card.Child;
            grid.Children[1].Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            grid.Children[2].Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            card.VerticalAlignment = hide ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        }

        // ================================================================== updates

        private void Tick()
        {
            if (_itemsReady && _rows.Count == 0 && _job.Items.Count > 0)
            {
                var root = (_job.Spec.Sources.Count == 1 && Directory.Exists(_job.Spec.Sources[0]) ? _job.Spec.Sources[0] : string.Empty);
                foreach (var item in _job.Items.ToList())
                {
                    var row = new FileRow(item, root);
                    _rowFor[item] = row;
                    _rows.Add(row);
                }
            }

            FileItem latest = null;
            foreach (var item in _changed.Keys.ToList())
            {
                _changed.TryRemove(item, out _);
                if (_rowFor.TryGetValue(item, out var row)) row.Update();
                if (item.State == FileState.Copying) latest = item;
            }
            if (latest != null && _rowFor.TryGetValue(latest, out var current) && Settings.ShowFileList) _list.ScrollIntoView(current);

            var p = _progress;
            _status.Text = _phaseText;
            Title = (p != null && _phase >= JobPhase.Copying && _phase < JobPhase.Finished ? $"{p.Fraction:P0} · " : string.Empty) + _job.Spec.Verbing + " · KUDUCOPY";
            SetChip();

            if (p == null || _phase < JobPhase.Copying)
            {
                _percent.Text = _phase == JobPhase.Waiting ? "Waiting" : "…";
                _amount.Text = _itemsReady ? $"{Format.Count(_job.Items.Count, "file")} · {Format.Size(_job.TotalBytes)}" : _phaseText;
                _bar.IsIndeterminate = true;
                TaskbarItemInfo.ProgressState = _job.IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Indeterminate;
                return;
            }

            _bar.IsIndeterminate = false;
            _bar.Value = p.Fraction;
            TaskbarItemInfo.ProgressState = _job.IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
            TaskbarItemInfo.ProgressValue = p.Fraction;
            _percent.Text = $"{Math.Floor(p.Fraction * 100):0}%";
            var verifying = _phase == JobPhase.Verifying;
            _amount.Text = verifying
                ? $"Verifying · {Format.Count(p.DoneFiles, "file")} of {p.TotalFiles:N0} checked"
                : $"{Format.Size(p.DoneBytes)} of {Format.Size(p.TotalBytes)} · {p.DoneFiles:N0} of {Format.Count(p.TotalFiles, "file")}";
            _speed.Text = _job.IsPaused ? "Paused" : Format.Speed(p.BytesPerSecond);
            _left.Text = _job.IsPaused ? "Press Resume to continue" : p.Remaining.HasValue ? "About " + Format.Duration(p.Remaining.Value) + " left" : string.Empty;

            var c = p.Current;
            if (c != null)
            {
                _nowName.Text = Path.GetFileName(c.File.Source);
                _nowAmount.Text = verifying ? Format.Size(c.File.Size) : $"{Format.Size((long)(c.File.Size * c.Percent / 100))} of {Format.Size(c.File.Size)}";
                _nowBar.Value = verifying ? 0 : c.Percent;
            }
            _skip.IsEnabled = _phase == JobPhase.Copying && c != null && !c.IsManual;
            UpdateCounts();
        }

        private void SetChip()
        {
            string text;
            var tone = Tone.Accent;
            switch (_phase)
            {
                case JobPhase.Waiting: text = "Waiting in queue"; tone = Tone.Neutral; break;
                case JobPhase.Scanning: text = "Finding files"; break;
                case JobPhase.Verifying: text = "Verifying"; break;
                default: text = _job.IsPaused ? "Paused" : null; tone = Tone.Warn; break;
            }
            var key = text + tone;
            if ((string)_chipHost.Tag == key) return;
            _chipHost.Tag = key;
            _chipHost.Child = text == null ? null : Ui.Chip(text, tone);
        }

        private void UpdateCounts()
        {
            var items = _job.Items;
            int done = 0, now = 0, retry = 0, wait = 0, failed = 0, skipped = 0;
            foreach (var i in items.ToList())
            {
                switch (i.State)
                {
                    case FileState.Copied:
                    case FileState.Verified: done++; break;
                    case FileState.Copying: now++; break;
                    case FileState.Retrying: retry++; break;
                    case FileState.Failed: failed++; break;
                    case FileState.Skipped: skipped++; break;
                    default: wait++; break;
                }
            }
            _counts.Children.Clear();
            void Chip(int n, string label, Tone tone)
            {
                if (n > 0) _counts.Children.Add(Ui.Chip($"{n:N0} {label}", tone).Margin(0, 0, 6, 0));
            }
            Chip(done, _phase == JobPhase.Verifying ? "done" : "copied", Tone.Ok);
            Chip(now, "copying", Tone.Accent);
            Chip(retry, "retrying", Tone.Warn);
            Chip(failed, "failed", Tone.Bad);
            Chip(skipped, "skipped", Tone.Neutral);
            Chip(wait, "waiting", Tone.Neutral);
            if (_job.SkippedExisting > 0) Chip(_job.SkippedExisting, "already there", Tone.Neutral);
        }

        // ================================================================== actions

        private void TogglePause()
        {
            if (_job.IsPaused)
            {
                _job.Resume();
                _pause.Content = Ui.Label("IconPause", "Pause");
            }
            else
            {
                _job.Pause();
                _pause.Content = Ui.Label("IconPlay", "Resume");
            }
        }

        private void CancelJob()
        {
            if (_finished)
            {
                Close();
                return;
            }
            if (!Dialogs.Confirm(this, "Stop this job? Files already copied stay where they are.")) return;
            _cancel.IsEnabled = false;
            _job.Cancel();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_finished) return;
            if (!Dialogs.Confirm(this, "This job is still running. Stop it and close?"))
            {
                e.Cancel = true;
                return;
            }
            _job.Cancel();
        }

        // ================================================================== finished

        private void Finished()
        {
            Tick();
            _finished = true;
            _timer.Stop();
            foreach (var row in _rows) row.Update();
            var r = _job.Record;
            TaskbarItemInfo.ProgressState = r.Result == JobResult.Done ? TaskbarItemProgressState.None : TaskbarItemProgressState.Error;
            if (r.Result != JobResult.Done) TaskbarItemInfo.ProgressValue = 1;
            Title = "Finished · KUDUCOPY";

            _content.Children.Clear();
            _content.Children.Add(BuildResultView(r));
            _status.Text = r.Result == JobResult.Done ? "Finished · saved in History" : r.Message;
            _statusRight.Text = string.Empty;
            App.JobFinished(this);
            DoAfterFinish(r);
        }

        private void DoAfterFinish(JobRecord r)
        {
            if (r.Result == JobResult.Cancelled) return;
            var others = JobQueue.CountRunning + JobQueue.CountWaiting > 0;
            switch (_job.Spec.After)
            {
                case AfterFinish.Close:
                    if (r.Result == JobResult.Done) Close();
                    break;
                case AfterFinish.Sound:
                    Power.Sound();
                    break;
                case AfterFinish.Sleep:
                case AfterFinish.Shutdown:
                    if (others)
                    {
                        _status.Text = "Not going to sleep or shutting down: other jobs are still running.";
                        break;
                    }
                    if (_job.Spec.After == AfterFinish.Sleep) Power.Sleep();
                    else Power.Shutdown();
                    break;
            }
        }

        private UIElement BuildResultView(JobRecord r)
        {
            var ok = r.Result == JobResult.Done;
            var icon = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Child = Ui.Icon(ok ? "IconCheck" : "IconWarn", 18)
            }.Res(Border.BackgroundProperty, ok ? "OkBg" : r.Result == JobResult.Cancelled ? "NeutralChipBg" : "WarnBg");
            icon.Res(TextElement.ForegroundProperty, ok ? "OkText" : r.Result == JobResult.Cancelled ? "SubtleText" : "WarnText");
            ((FrameworkElement)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;

            var verb = _job.Spec.Mode == CopyMode.Move ? "Moved" : "Copied";
            string title;
            switch (r.Result)
            {
                case JobResult.Done:
                    title = r.Files == 0 ? r.Message : $"{verb} {Format.Count(r.Copied, "file")} to {r.To}";
                    break;
                case JobResult.DoneWithProblems:
                    title = $"{verb} {r.Copied:N0} of {Format.Count(r.Files, "file")} to {r.To}";
                    break;
                case JobResult.Cancelled:
                    title = $"Cancelled · {r.Copied:N0} of {Format.Count(r.Files, "file")} {verb.ToLowerInvariant()}";
                    break;
                default:
                    title = "The job couldn't finish";
                    break;
            }
            var subtitle = r.Result == JobResult.Done
                ? (r.Verified ? "Every copy was verified." : r.Skipped > 0 ? $"{Format.Count(r.Skipped, "file")} already there or skipped." : "Everything was copied.")
                : r.Message;

            var head = Ui.Columns(Ui.Auto, Ui.Star());
            head.Children.Add(icon.Margin(0, 0, 14, 0).At(0, 0));
            head.Children.Add(Ui.Column(Ui.Text(title, 16, bold: true), Ui.Subtle(subtitle, 12.5)).At(0, 1));

            var stats = new UniformGrid { Rows = 1, Margin = new Thickness(0, 16, 0, 0) };
            void Stat(string value, string label, string brush)
            {
                stats.Children.Add(Ui.Card(Ui.Column(Ui.Text(value, 22, brush, bold: true), Ui.Subtle(label, 12, false)), 14).Margin(0, 0, 10, 0));
            }
            var elapsed = r.Finished - r.Started;
            Stat(r.Copied.ToString("N0"), verb.ToLowerInvariant(), "OkText");
            if (_job.Spec.Verify) Stat(_job.Items.Count(i => i.State == FileState.Verified).ToString("N0"), $"verified ({Checksums.Name(_job.Spec.Hash)}) ✓", "OkText");
            else Stat(r.Skipped.ToString("N0"), "skipped or already there", "Text");
            Stat(r.Failed.Count.ToString("N0"), "couldn't be copied", r.Failed.Count > 0 ? "BadText" : "Text");
            var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
            Stat(Format.Duration(elapsed), r.Bytes > 0 ? "at " + Format.Speed(r.Bytes / seconds) + " average" : "in total", "Text");
            ((FrameworkElement)stats.Children[stats.Children.Count - 1]).Margin = new Thickness(0);

            var body = Ui.Column(head, stats);

            if (r.Failed.Count > 0)
            {
                var list = new StackPanel();
                foreach (var f in r.Failed.Take(500))
                {
                    var row = Ui.Columns(Ui.Px(30), Ui.Star(), Ui.Star(), Ui.Star(1.2));
                    var x = Ui.Icon("IconClose", 14).Margin(12, 0, 0, 0);
                    x.Res(TextElement.ForegroundProperty, "BadText");
                    row.Children.Add(x.At(0, 0));
                    row.Children.Add(Ui.Text(Path.GetFileName(f.Source), 12.5, bold: true).Tip(f.Source).At(0, 1));
                    row.Children.Add(Ui.Subtle(Path.GetDirectoryName(f.Source), 12.5, false).Margin(8, 0, 8, 0).At(0, 2));
                    row.Children.Add(Ui.Text(f.Reason, 12.5, "BadText", wrap: true).At(0, 3));
                    list.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8) }
                        .Res(Border.BorderBrushProperty, "BorderBrush"));
                }
                var head2 = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
                head2.Children.Add(Ui.Heading("Couldn't be copied", 13).At(0, 0));
                head2.Children.Add(Ui.Subtle("Close any programs using these files, then retry.", 12, false).Margin(10, 0, 0, 0).At(0, 1));
                head2.Children.Add(Ui.Small(Ui.Button("Copy list", "IconCopy", click: (s, e) =>
                    Ui.Copy(string.Join(Environment.NewLine, r.Failed.Select(f => f.Source + "\t" + f.Reason))))).At(0, 2));
                var card = Ui.Card(Ui.Column(head2.Margin(14, 10, 14, 10), Ui.Divider(),
                    new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }), 0);
                body.Children.Add(card.Margin(0, 14, 0, 0));
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            if (r.Failed.Count > 0)
            {
                buttons.Children.Add(Ui.Button($"Retry {Format.Count(r.Failed.Count, "failed file")}", "IconRefresh", "PrimaryButton", (s, e) =>
                {
                    App.StartJob(Retry.For(_job.Spec, r.Failed), this);
                }).Margin(0, 0, 8, 8));
            }
            buttons.Children.Add(Ui.Button("Open destination", "IconFolder", click: (s, e) => AppInfo.OpenFolder(r.To)).Margin(0, 0, 8, 8));
            if (_job.Hashes.Count > 0)
            {
                buttons.Children.Add(Ui.Button("Save checksums…", "IconFile", click: (s, e) => SaveChecksums()).Margin(0, 0, 8, 8));
            }
            buttons.Children.Add(Ui.Button("View log", "IconLog", "GhostButton", (s, e) => AppInfo.OpenFile(r.LogPath)).Margin(0, 0, 8, 8));
            var close = Ui.Button("Close", null, click: (s, e) => Close());
            close.IsCancel = true;
            buttons.Children.Add(close.Margin(0, 0, 0, 8));
            body.Children.Add(buttons);

            if (_job.Spec.After == AfterFinish.Shutdown && r.Result != JobResult.Cancelled)
            {
                body.Children.Add(Ui.Banner("IconWarn", Ui.Row(Ui.Text("Windows will shut down in 60 seconds.  ", 12.5),
                    Ui.Link("Cancel shutdown", (s, e) => Power.CancelShutdown())), "WarnBg", "WarnText").Margin(0, 6, 0, 0));
            }

            var grid = new Grid { Margin = new Thickness(22, 18, 22, 16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(body.At(0));
            if (_rows.Count > 0 && Settings.ShowFileList)
            {
                ((Panel)_listCard.Parent)?.Children.Remove(_listCard);
                grid.Children.Add(_listCard.Margin(0, 14, 0, 0).At(1));
            }
            return new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void SaveChecksums()
        {
            try
            {
                var folder = Planner.Full(_job.Spec.Destination);
                var path = Checksums.Write(folder, _job.Spec.Hash, _job.Hashes.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase));
                AppInfo.OpenFolder(folder, path);
                _status.Text = "Saved " + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "Couldn't save the checksum file:\n\n" + ex.Message);
            }
        }
    }
}
