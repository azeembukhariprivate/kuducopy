using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    public sealed class PreviewRow
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string SizeText { get; set; }
        public string ActionText { get; set; }
        public string Tone { get; set; }
        public string Note { get; set; }
        public PreviewAction Action { get; set; }
    }

    /// <summary>Shows what a job will do before it starts: new, replaced, kept, skipped and deleted files. Nothing is copied.</summary>
    public sealed class PreviewWindow : Window
    {
        private const string RowTemplate = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Height='30'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='120'/><ColumnDefinition Width='*'/><ColumnDefinition Width='0.9*'/><ColumnDefinition Width='86'/><ColumnDefinition Width='0.8*'/>
    </Grid.ColumnDefinitions>
    <Border x:Name='Chip' CornerRadius='10' Padding='8,2,8,3' Margin='14,0,0,0' HorizontalAlignment='Left' VerticalAlignment='Center' Background='{DynamicResource NeutralChipBg}'>
      <TextBlock x:Name='ChipText' Text='{Binding ActionText}' FontSize='11.5' FontWeight='SemiBold' Foreground='{DynamicResource SubtleText}'/>
    </Border>
    <TextBlock Grid.Column='1' Text='{Binding Name}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,8,0' FontWeight='Medium'/>
    <TextBlock Grid.Column='2' Text='{Binding Folder}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' Margin='0,0,8,0'/>
    <TextBlock Grid.Column='3' Text='{Binding SizeText}' VerticalAlignment='Center' HorizontalAlignment='Right' Foreground='{DynamicResource SubtleText}' Margin='0,0,14,0'/>
    <TextBlock Grid.Column='4' Text='{Binding Note}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}'/>
  </Grid>
  <DataTemplate.Triggers>
    <DataTrigger Binding='{Binding Tone}' Value='ok'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource OkBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource OkText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='accent'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource ChipBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource ChipText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='warn'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource WarnBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource WarnText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='bad'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource BadBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource BadText}'/>
    </DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>";

        private readonly JobSpec _spec;
        private readonly CopyJob _job;
        private readonly Action _started;
        private readonly Grid _content = new Grid { Margin = new Thickness(22, 18, 22, 16) };
        private readonly ListBox _list = new ListBox().Styled("TableList");
        private readonly WrapPanel _filters = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        private PreviewResult _result;
        private List<PreviewRow> _rows = new List<PreviewRow>();
        private PreviewAction? _filter;

        public PreviewWindow(JobSpec spec, Window owner, Action started = null)
        {
            _spec = spec.Clone();
            _job = new CopyJob(_spec);
            _started = started;
            Width = 960;
            Height = 700;
            MinWidth = 760;
            MinHeight = 480;
            Dialogs.Prepare(this, "Preview · KUDUCOPY", owner);

            _list.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);

            Content = _content;
            ShowLoading();
            Loaded += async (s, e) =>
            {
                try
                {
                    _result = await _job.PreviewAsync();
                    ShowResult();
                }
                catch (OperationCanceledException)
                {
                    Close();
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            };
            Closing += (s, e) => { if (_result == null) _job.Cancel(); };
        }

        public bool IsReady => _result != null;
        public PreviewResult Result => _result;

        private void ShowLoading()
        {
            _content.Children.Clear();
            var bar = new ProgressBar { Height = 6, IsIndeterminate = true, Width = 320, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = Ui.Button("Cancel", null, click: (s, e) => { _job.Cancel(); Close(); });
            cancel.HorizontalAlignment = HorizontalAlignment.Center;
            var panel = Ui.Column(new LogoMark(48).Res(System.Windows.Documents.TextElement.ForegroundProperty, "Accent"),
                Ui.Heading("Listing what would be copied…", 16).Margin(0, 14, 0, 0),
                Ui.Subtle("Robocopy is checking the files in list-only mode. Nothing is copied.", 12.5).Margin(0, 4, 0, 0), bar, cancel.Margin(0, 18, 0, 0));
            foreach (FrameworkElement c in panel.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            _content.Children.Add(panel);
        }

        private void ShowError(string message)
        {
            _content.Children.Clear();
            var close = Ui.Button("Close", null, "PrimaryButton", (s, e) => Close());
            close.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(Ui.Column(Ui.Heading("The preview couldn't be made", 16), Ui.Text(message, 13, wrap: true).Margin(0, 8, 0, 0), close.Margin(0, 16, 0, 0)));
        }

        private static (string Text, string Tone) Describe(PreviewAction a)
        {
            switch (a)
            {
                case PreviewAction.Copy: return ("New", "ok");
                case PreviewAction.Replace: return ("Replace", "accent");
                case PreviewAction.KeepBoth: return ("Keep both", "accent");
                case PreviewAction.Ask: return ("You'll be asked", "warn");
                case PreviewAction.Delete: return ("Delete", "bad");
                default: return ("Skip", "neutral");
            }
        }

        private void ShowResult()
        {
            var r = _result;
            var root = string.Empty;
            _rows = r.Entries.Select(e =>
            {
                var path = e.Source ?? e.Dest;
                var (text, tone) = Describe(e.Action);
                return new PreviewRow
                {
                    Name = Path.GetFileName(path),
                    Folder = Path.GetDirectoryName(path) ?? string.Empty,
                    SizeText = Format.Size(e.Size),
                    ActionText = text,
                    Tone = tone,
                    Note = e.Action == PreviewAction.Delete ? "Deleted from " + Path.GetDirectoryName(e.Dest) : e.Note,
                    Action = e.Action
                };
            }).ToList();

            _content.Children.Clear();
            _content.RowDefinitions.Clear();
            for (var i = 0; i < 4; i++) _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _content.RowDefinitions.Add(new RowDefinition());
            _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var verb = _spec.Verb;
            _content.Children.Add(Ui.Column(
                Ui.Text($"Preview: {verb.ToLowerInvariant()} {_spec.SourceSummary} to {_spec.Destination}", 16, bold: true),
                Ui.Subtle("Nothing has been copied yet. This is what Start would do" + (_spec.Advanced ? " with your Advanced options." : "."), 12.5)).At(0));

            var stats = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
            void Stat(PreviewAction a, string label, string brush)
            {
                var n = r.Count(a);
                if (n == 0 && a != PreviewAction.Copy) return;
                stats.Children.Add(Ui.Card(Ui.Column(Ui.Text(n.ToString("N0"), 22, n > 0 ? brush : "Text", bold: true),
                    Ui.Subtle($"{label} · {Format.Size(r.Bytes(a))}", 12, false)), 14).Margin(0, 0, 10, 0));
            }
            Stat(PreviewAction.Copy, "new files", "OkText");
            Stat(PreviewAction.Replace, "will be replaced", "LinkText");
            Stat(PreviewAction.KeepBoth, "kept as new copies", "LinkText");
            Stat(PreviewAction.Ask, "already there (you'll be asked)", "WarnText");
            Stat(PreviewAction.Skip, "skipped", "Text");
            Stat(PreviewAction.Delete, "DELETED from the destination", "BadText");
            if (stats.Children.Count > 0) ((FrameworkElement)stats.Children[stats.Children.Count - 1]).Margin = new Thickness(0);
            _content.Children.Add(stats.At(1));

            var notes = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var space = SpaceNote(r.BytesToWrite);
            if (space != null) notes.Children.Add(space);
            if (r.Count(PreviewAction.Delete) > 0)
                notes.Children.Add(Ui.Banner("IconWarn", Ui.Text($"{Format.Count(r.Count(PreviewAction.Delete), "file")} in {_spec.Destination} " +
                    (_spec.Mode == CopyMode.Mirror ? "will be DELETED by Mirror, because they aren't in the source." : "will be DELETED because /MIR or /PURGE is on. They aren't in the source."),
                    12.5, wrap: true), "BadBg", "BadText").Margin(0, 8, 0, 0));
            if (_spec.Mode == CopyMode.Move)
                notes.Children.Add(Ui.Subtle("Moving: the originals are removed after they're copied" + (_spec.Verify ? " and verified." : "."), 12.5).Margin(0, 8, 0, 0));
            _content.Children.Add(notes.At(2));

            BuildFilters();
            var head = Ui.Columns(Ui.Auto, Ui.Star());
            head.Children.Add(Ui.Heading("Files", 13).At(0, 0));
            head.Children.Add(_filters.Margin(12, 0, 0, 0).At(0, 1));
            _content.Children.Add(head.Margin(0, 14, 0, 8).At(3));

            var cols = Ui.Columns(Ui.Px(120), Ui.Star(), Ui.Star(0.9), Ui.Px(86), Ui.Star(0.8));
            var titles = new[] { "What happens", "Name", "Folder", "Size", "Why" };
            for (var i = 0; i < titles.Length; i++)
            {
                var t = Ui.Text(titles[i], 12, "SubtleText", bold: true).Margin(i == 0 ? 14 : 0, 7, i == 3 ? 14 : 8, 7).At(0, i);
                if (i == 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                cols.Children.Add(t);
            }
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition());
            table.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = cols }.Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            table.Children.Add(_list.At(1));
            if (_rows.Count == 0)
                table.Children.Add(Ui.Subtle("Nothing to copy: the sources are empty, or everything was left out by your options.", 13).Margin(16, 16, 16, 16).At(1));
            var card = Ui.Card(table, 0);
            card.ClipToBounds = true;
            _content.Children.Add(card.At(4));
            FillList();

            var start = Ui.Button("Start " + _spec.Verbing.ToLowerInvariant(), _spec.Mode == CopyMode.Move ? "IconMove" : "IconCopy", "PrimaryButton", (s, e) => StartJob());
            start.IsEnabled = r.Entries.Any(e => e.Action != PreviewAction.Skip);
            if (!start.IsEnabled) start.ToolTip = "Nothing to do: the destination is already up to date.";
            var copy = Ui.Button("Copy list", "IconCopy", click: (s, e) =>
                Ui.Copy(string.Join(Environment.NewLine, _rows.Select(x => $"{x.ActionText}\t{Path.Combine(x.Folder, x.Name)}\t{x.SizeText}\t{x.Note}"))));
            var close = Ui.Button("Close", null, "GhostButton", (s, e) => Close());
            close.IsCancel = true;
            var buttons = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
            buttons.Children.Add(start.At(0, 0));
            buttons.Children.Add(copy.Margin(8, 0, 0, 0).At(0, 1));
            buttons.Children.Add(close.At(0, 3));
            _content.Children.Add(buttons.Margin(0, 14, 0, 0).At(5));
        }

        private UIElement SpaceNote(long bytes)
        {
            try
            {
                var root = Path.GetPathRoot(Planner.Full(_spec.Destination));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return null;
                var after = drive.AvailableFreeSpace - bytes;
                return after >= 0
                    ? Ui.Subtle($"Writes up to {Format.Size(bytes)}. {root.TrimEnd('\\')} has {Format.Size(drive.AvailableFreeSpace)} free, so there's enough space ✓", 12.5)
                    : (UIElement)Ui.Banner("IconWarn", Ui.Text($"Not enough space: this writes up to {Format.Size(bytes)}, but {root.TrimEnd('\\')} only has {Format.Size(drive.AvailableFreeSpace)} free.",
                        12.5, wrap: true), "WarnBg", "WarnText");
            }
            catch
            {
                return null;
            }
        }

        private void BuildFilters()
        {
            _filters.Children.Clear();
            void Chip(string label, PreviewAction? action, int count)
            {
                if (action != null && count == 0) return;
                var t = new ToggleButton { Content = $"{label} · {count:N0}", IsChecked = _filter == action }.Styled("ChipToggle");
                t.Click += (s, e) =>
                {
                    _filter = action;
                    BuildFilters();
                    FillList();
                };
                _filters.Children.Add(t);
            }
            Chip("All", null, _rows.Count);
            foreach (PreviewAction a in Enum.GetValues(typeof(PreviewAction))) Chip(Describe(a).Text, a, _rows.Count(r => r.Action == a));
        }

        private void FillList() => _list.ItemsSource = _filter == null ? _rows : _rows.Where(r => r.Action == _filter).ToList();

        private void StartJob()
        {
            if (_result.Count(PreviewAction.Delete) > 0 &&
                !Dialogs.Confirm(this, $"{Format.Count(_result.Count(PreviewAction.Delete), "file")} in {_spec.Destination} will be DELETED. Start anyway?"))
                return;
            if (App.StartJob(_spec, Owner ?? this))
            {
                _started?.Invoke();
                Close();
            }
        }
    }
}
