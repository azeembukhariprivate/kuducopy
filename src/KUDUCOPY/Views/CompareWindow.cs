using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    public sealed class CompareRow
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string StateText { get; set; }
        public string Tone { get; set; }
        public string LeftText { get; set; }
        public string RightText { get; set; }
        public CompareEntry Entry { get; set; }
    }

    /// <summary>Two folders side by side: which files are only on one side, newer, different or the same. Nothing is copied unless the user asks.</summary>
    public sealed class CompareWindow : Window
    {
        private const string RowTemplate = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Height='30'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='140'/><ColumnDefinition Width='*'/><ColumnDefinition Width='0.8*'/><ColumnDefinition Width='170'/><ColumnDefinition Width='170'/>
    </Grid.ColumnDefinitions>
    <Border x:Name='Chip' CornerRadius='10' Padding='8,2,8,3' Margin='14,0,0,0' HorizontalAlignment='Left' VerticalAlignment='Center' Background='{DynamicResource NeutralChipBg}'>
      <TextBlock x:Name='ChipText' Text='{Binding StateText}' FontSize='11.5' FontWeight='SemiBold' Foreground='{DynamicResource SubtleText}'/>
    </Border>
    <TextBlock Grid.Column='1' Text='{Binding Name}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,8,0' FontWeight='Medium'/>
    <TextBlock Grid.Column='2' Text='{Binding Folder}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' Margin='0,0,8,0'/>
    <TextBlock Grid.Column='3' Text='{Binding LeftText}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' Margin='0,0,8,0'/>
    <TextBlock Grid.Column='4' Text='{Binding RightText}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' Margin='0,0,8,0'/>
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
  </DataTemplate.Triggers>
</DataTemplate>";

        private readonly TextBox _left = new TextBox { MinHeight = 34, FontSize = 13 };
        private readonly TextBox _right = new TextBox { MinHeight = 34, FontSize = 13 };
        private readonly CheckBox _contents = new CheckBox { Content = "Also compare file contents (slower)" }.Styled("Switch");
        private readonly Button _compare;
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Extended }.Styled("TableList");
        private readonly WrapPanel _filters = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        private readonly UniformGrid _stats = new UniformGrid { Rows = 1 };
        private readonly StackPanel _notes = new StackPanel();
        private readonly Grid _body = new Grid();
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12, false);
        private readonly Button _toRight;
        private readonly Button _toLeft;
        private readonly Button _copyList;
        private readonly Button _save;
        private CancellationTokenSource _cancel;
        private CompareResult _result;
        private List<CompareRow> _rows = new List<CompareRow>();
        private CompareState? _filter;

        public CompareWindow(Window owner, string left = null, string right = null)
        {
            Width = 1040;
            Height = 720;
            MinWidth = 820;
            MinHeight = 520;
            Dialogs.Prepare(this, "Compare folders · KUDUCOPY", owner);

            _left.Text = left ?? string.Empty;
            _right.Text = right ?? string.Empty;
            _left.Res(Control.FontFamilyProperty, "MonoFont");
            _right.Res(Control.FontFamilyProperty, "MonoFont");
            _contents.ToolTip = "Reads files of the same size on both sides and compares their checksums, to find changes that kept the same size and date.";
            _list.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);
            _list.SelectionChanged += (s, e) => RefreshButtons();

            _compare = Ui.Button("Compare", "IconCompare", "PrimaryButton", (s, e) => RunCompare());
            _compare.IsDefault = true;
            _toRight = Ui.Button("Copy selected to the right", "IconCopy", click: (s, e) => CopySelected(true));
            _toLeft = Ui.Button("Copy selected to the left", "IconCopy", click: (s, e) => CopySelected(false));
            _copyList = Ui.Button("Copy list", "IconCopy", "GhostButton", (s, e) => Ui.Copy(FolderCompare.Report(_result)));
            _save = Ui.Button("Save report…", "IconSave", "GhostButton", (s, e) => SaveReport());

            var root = new Grid { Margin = new Thickness(22, 18, 22, 16) };
            for (var i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(Ui.Column(Ui.Text("Compare two folders", 16, bold: true),
                Ui.Subtle("See which files are only on one side, newer, or different. Nothing is copied unless you choose to.", 12.5)).At(0));

            var pick = Ui.Columns(Ui.Px(52), Ui.Star(), Ui.Auto);
            pick.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pick.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Side(int row, string label, TextBox box)
            {
                pick.Children.Add(Ui.Text(label, 13, bold: true).Margin(0, 0, 8, 0).At(row, 0));
                ((FrameworkElement)pick.Children[pick.Children.Count - 1]).VerticalAlignment = VerticalAlignment.Center;
                pick.Children.Add(box.Margin(0, row == 0 ? 0 : 8, 0, 0).At(row, 1));
                pick.Children.Add(Ui.Button("Browse…", "IconFolder", click: (s, e) =>
                {
                    var folder = Dialogs.PickFolder(this, "Choose the " + label.ToLowerInvariant() + " folder", box.Text);
                    if (folder != null) box.Text = folder;
                }).Margin(8, row == 0 ? 0 : 8, 0, 0).At(row, 2));
            }
            Side(0, "Left", _left);
            Side(1, "Right", _right);
            var swap = Ui.Button("Swap", "IconRefresh", "GhostButton", (s, e) =>
            {
                var t = _left.Text;
                _left.Text = _right.Text;
                _right.Text = t;
            });
            var actions = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            actions.Children.Add(_contents.At(0, 0));
            _contents.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(swap.At(0, 2));
            actions.Children.Add(_compare.Margin(8, 0, 0, 0).At(0, 3));
            root.Children.Add(Ui.Card(Ui.Column(pick, actions.Margin(0, 12, 0, 0)), 16).Margin(0, 14, 0, 0).At(1));

            root.Children.Add(Ui.Column(_stats.Margin(0, 14, 0, 0), _notes).At(2));

            var head = Ui.Columns(Ui.Auto, Ui.Star());
            head.Children.Add(Ui.Heading("Files", 13).At(0, 0));
            head.Children.Add(_filters.Margin(12, 0, 0, 0).At(0, 1));
            root.Children.Add(head.Margin(0, 14, 0, 8).At(3));

            var cols = Ui.Columns(Ui.Px(140), Ui.Star(), Ui.Star(0.8), Ui.Px(170), Ui.Px(170));
            var titles = new[] { "State", "Name", "Folder", "Left", "Right" };
            for (var i = 0; i < titles.Length; i++)
                cols.Children.Add(Ui.Text(titles[i], 12, "SubtleText", bold: true).Margin(i == 0 ? 14 : 0, 7, 8, 7).At(0, i));
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition());
            table.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = cols }.Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            table.Children.Add(_list.At(1));
            table.Children.Add(_body.At(1));
            var card = Ui.Card(table, 0);
            card.ClipToBounds = true;
            root.Children.Add(card.At(4));

            var close = Ui.Button("Close", null, "GhostButton", (s, e) => Close());
            close.IsCancel = true;
            var buttons = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
            buttons.Children.Add(_toRight.At(0, 0));
            buttons.Children.Add(_toLeft.Margin(8, 0, 0, 0).At(0, 1));
            buttons.Children.Add(_copyList.Margin(8, 0, 0, 0).At(0, 2));
            buttons.Children.Add(_save.Margin(4, 0, 0, 0).At(0, 3));
            buttons.Children.Add(close.At(0, 5));
            root.Children.Add(Ui.Column(_status, buttons.Margin(0, 8, 0, 0)).Margin(0, 12, 0, 0).At(5));

            Content = root;
            ShowMessage("Choose two folders and press Compare.");
            RefreshButtons();
            Loaded += (s, e) => { if (_left.Text.Length > 0 && _right.Text.Length > 0) RunCompare(); };
            Closing += (s, e) => _cancel?.Cancel();
        }

        public bool IsBusy => _cancel != null;
        public CompareResult Result => _result;

        private void ShowMessage(string text, bool busy = false)
        {
            _body.Children.Clear();
            var panel = Ui.Column(Ui.Subtle(text, 13));
            if (busy) panel.Children.Add(new ProgressBar { Height = 6, IsIndeterminate = true, Width = 280, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
            _body.Children.Add(panel.Margin(16, 16, 16, 16));
            _body.Visibility = Visibility.Visible;
        }

        private async void RunCompare()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                return;
            }
            var left = _left.Text.Trim().Trim('"');
            var right = _right.Text.Trim().Trim('"');
            if (left.Length == 0 || right.Length == 0)
            {
                Dialogs.Error(this, "Choose a folder for both sides first.");
                return;
            }

            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            var contents = _contents.IsChecked == true;
            _compare.Content = Ui.Label("IconStop", "Stop");
            _result = null;
            _rows = new List<CompareRow>();
            _list.ItemsSource = null;
            _stats.Children.Clear();
            _notes.Children.Clear();
            _filters.Children.Clear();
            ShowMessage(contents ? "Comparing, and reading files to compare their contents…" : "Comparing…", true);
            RefreshButtons();

            var last = DateTime.MinValue;
            Action<string> progress = path =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 200) return;
                last = DateTime.UtcNow;
                Dispatcher.BeginInvoke(new Action(() => { if (_cancel != null) _status.Text = path; }));
            };
            try
            {
                _result = await Task.Run(() => FolderCompare.Run(left, right, contents, progress, token));
                ShowResult();
            }
            catch (OperationCanceledException)
            {
                ShowMessage("Stopped. Press Compare to start again.");
            }
            catch (Exception ex)
            {
                ShowMessage(ex is InvalidOperationException ? ex.Message : "The folders couldn't be compared:\n" + ex.Message);
            }
            finally
            {
                _cancel = null;
                _compare.Content = Ui.Label("IconCompare", "Compare");
                _status.Text = _result == null ? string.Empty : (_result.Contents ? "Compared size, date and contents." : "Compared size and date. Files whose dates are within 2 seconds count as the same.");
                RefreshButtons();
            }
        }

        private static (string Text, string Tone) Describe(CompareState s)
        {
            switch (s)
            {
                case CompareState.LeftOnly: return ("Only left", "ok");
                case CompareState.RightOnly: return ("Only right", "ok");
                case CompareState.LeftNewer: return ("Left newer", "accent");
                case CompareState.RightNewer: return ("Right newer", "accent");
                case CompareState.Different: return ("Different", "warn");
                default: return ("Same", "neutral");
            }
        }

        private static string SideText(long size, DateTime time) =>
            size < 0 ? "–" : Format.Size(size) + " · " + time.ToLocalTime().ToString("dd MMM yyyy HH:mm");

        private void ShowResult()
        {
            var r = _result;
            _rows = r.Entries.Select(e =>
            {
                var (text, tone) = Describe(e.State);
                return new CompareRow
                {
                    Name = Path.GetFileName(e.Relative),
                    Folder = Path.GetDirectoryName(e.Relative) ?? string.Empty,
                    StateText = e.SameContent ? "Same content" : text,
                    Tone = tone,
                    LeftText = SideText(e.LeftSize, e.LeftTime),
                    RightText = SideText(e.RightSize, e.RightTime),
                    Entry = e
                };
            }).ToList();

            _stats.Children.Clear();
            foreach (CompareState s in Enum.GetValues(typeof(CompareState)))
            {
                var n = r.Count(s);
                var brush = n == 0 ? "Text" : s == CompareState.Same ? "Text" : s == CompareState.Different ? "WarnText" : s == CompareState.LeftOnly || s == CompareState.RightOnly ? "OkText" : "LinkText";
                _stats.Children.Add(Ui.Card(Ui.Column(Ui.Text(n.ToString("N0"), 22, brush, bold: true),
                    Ui.Subtle(FolderCompare.Describe(s).ToLowerInvariant(), 12, false)), 14).Margin(0, 0, 10, 0));
            }
            ((FrameworkElement)_stats.Children[_stats.Children.Count - 1]).Margin = new Thickness(0);

            _notes.Children.Clear();
            if (r.Identical)
                _notes.Children.Add(Ui.Banner("IconCheck", Ui.Text($"The folders match: {Format.Count(r.Entries.Count, "file")}, all the same.", 12.5, wrap: true), "OkBg", "OkText").Margin(0, 12, 0, 0));
            if (r.Problems.Count > 0)
                _notes.Children.Add(Ui.Banner("IconWarn", Ui.Text($"{Format.Count(r.Problems.Count, "item")} couldn't be read, so the comparison may be incomplete. First: {r.Problems[0]}",
                    12.5, wrap: true), "WarnBg", "WarnText").Margin(0, 12, 0, 0));

            _filter = null;
            BuildFilters();
            FillList();
            if (_rows.Count == 0) ShowMessage("Both folders are empty.");
            else _body.Visibility = Visibility.Collapsed;
        }

        private void BuildFilters()
        {
            _filters.Children.Clear();
            void Chip(string label, CompareState? state, int count)
            {
                if (state != null && count == 0) return;
                var t = new ToggleButton { Content = $"{label} · {count:N0}", IsChecked = _filter == state }.Styled("ChipToggle");
                t.Click += (s, e) =>
                {
                    _filter = state;
                    BuildFilters();
                    FillList();
                };
                _filters.Children.Add(t);
            }
            Chip("All", null, _rows.Count);
            foreach (CompareState s in Enum.GetValues(typeof(CompareState))) Chip(Describe(s).Text, s, _rows.Count(r => r.Entry.State == s));
        }

        private void FillList()
        {
            _list.ItemsSource = _filter == null ? _rows : _rows.Where(r => r.Entry.State == _filter).ToList();
            RefreshButtons();
        }

        private List<CompareEntry> Selected() => _list.SelectedItems.Cast<CompareRow>().Select(r => r.Entry).ToList();

        private void RefreshButtons()
        {
            var selected = _result == null ? new List<CompareEntry>() : Selected();
            var right = selected.Count(e => e.OnLeft);
            var left = selected.Count(e => e.OnRight);
            _toRight.IsEnabled = right > 0 && _cancel == null;
            _toLeft.IsEnabled = left > 0 && _cancel == null;
            _toRight.Content = Ui.Label("IconCopy", right > 0 ? $"Copy {Format.Count(right, "file")} to the right" : "Copy selected to the right");
            _toLeft.Content = Ui.Label("IconCopy", left > 0 ? $"Copy {Format.Count(left, "file")} to the left" : "Copy selected to the left");
            _copyList.IsEnabled = _save.IsEnabled = _result != null && _cancel == null;
            if (_result != null && _cancel == null && selected.Count == 0)
                _status.Text = "Select files to copy them across (Ctrl+A selects everything shown, Shift or Ctrl+click for more).";
        }

        private void CopySelected(bool toRight)
        {
            var entries = Selected().Where(e => toRight ? e.OnLeft : e.OnRight).ToList();
            if (entries.Count == 0) return;
            var target = toRight ? _result.Right : _result.Left;
            var newer = FolderCompare.ReplacesNewer(entries, toRight);
            var replaced = entries.Count(e => e.OnLeft && e.OnRight);
            var message = $"Copy {Format.Count(entries.Count, "file")} to {target}?";
            if (replaced > 0) message += $"\n\n{Format.Count(replaced, "file")} there will be replaced.";
            if (newer > 0) message += $"\n{Format.Count(newer, "of them is", "of them are")} NEWER than the file you're copying over it.";
            if (!Dialogs.Confirm(this, message)) return;

            var spec = FolderCompare.CopySpec(_result, entries, toRight);
            if (App.StartJob(spec, this))
                _status.Text = "Copying in a separate window. Press Compare again when it's finished to see the result.";
        }

        private void SaveReport()
        {
            var path = Dialogs.SaveFile(this, "Save the comparison", "Compare " + Planner.DisplayName(_result.Left) + " and " + Planner.DisplayName(_result.Right) + ".txt",
                "Text file (tab separated)|*.txt|All files|*.*");
            if (path == null) return;
            try
            {
                File.WriteAllText(path, FolderCompare.Report(_result), System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "The report couldn't be saved:\n\n" + ex.Message);
            }
        }
    }
}
