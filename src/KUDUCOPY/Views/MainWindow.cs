using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    /// <summary>The main window: what to copy, where to, and how (Standard or Advanced mode).</summary>
    public sealed class MainWindow : Window
    {
        private readonly Grid _body = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _statusRight = Ui.Subtle(string.Empty, 12, false);
        private readonly RadioButton _copy = Seg("Copy");
        private readonly RadioButton _move = Seg("Move");
        private readonly RadioButton _mirror = Seg("Mirror");
        private readonly TextBlock _modeNote = Ui.Subtle(string.Empty, 11.5);
        private Border _conflictBox;
        private readonly RadioButton _standard = Seg("Standard");
        private readonly RadioButton _advanced = Seg("Advanced");

        // Sources
        private readonly ListBox _sources = new ListBox().Styled("TableList");
        private readonly TextBlock _sourceTotals = Ui.Subtle(string.Empty, 12, false);
        private readonly Dictionary<string, (int Files, long Bytes)> _sizes = new Dictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _counting;

        // Destination
        private readonly TextBox _dest = new TextBox { MinHeight = 36, FontSize = 13 };
        private readonly WrapPanel _favourites = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        private readonly TextBlock _driveName = Ui.Text(string.Empty, 12);
        private readonly TextBlock _driveFree = Ui.Subtle(string.Empty, 12, false);
        private readonly ProgressBar _driveBar = new ProgressBar { Height = 6, Maximum = 1 };
        private readonly TextBlock _driveAfter = Ui.Subtle(string.Empty, 11.5, false);
        private readonly StackPanel _driveBox = new StackPanel();

        // Standard options
        private readonly List<RadioButton> _conflict = new List<RadioButton>();
        private readonly CheckBox _verify = new CheckBox { Content = "Verify copies" }.Styled("Switch");
        private readonly ComboBox _hash = new ComboBox { Width = 120, MinHeight = 32, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock _verifyHelp = Ui.Subtle(string.Empty, 11.5);
        private readonly CheckBox _keepGoing = new CheckBox { Content = "Keep going if a file can't be copied" }.Styled("Switch");
        private readonly List<RadioButton> _after = new List<RadioButton>();

        // Advanced
        private readonly StackPanel _nav = new StackPanel();
        private readonly StackPanel _optionList = new StackPanel();
        private readonly TextBlock _groupTitle = Ui.Heading(string.Empty, 13);
        private readonly TextBlock _groupHint = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBox _search = new TextBox { Width = 220, MinHeight = 30, FontSize = 12 };
        private readonly TextBlock _command = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 };
        private readonly TextBlock _commandNote = Ui.Subtle(string.Empty, 11.5);
        private readonly TextBox _extra = new TextBox { MinHeight = 34 };
        private readonly TextBlock _advWhat = Ui.Text(string.Empty, 12.5, bold: true, wrap: true);
        private readonly TextBlock _advErrors = Ui.Text(string.Empty, 12, "BadText", wrap: true);
        private string _group = RoboOptions.GroupCopy;
        private HashSet<string> _supported;
        private string _robocopyVersion = string.Empty;

        // Bottom bar
        private readonly TextBlock _summary = Ui.Text(string.Empty, 13.5, bold: true);
        private readonly TextBlock _summaryNote = Ui.Subtle(string.Empty, 12, false);
        private readonly Button _start;

        private UIElement _standardView;
        private UIElement _advancedView;
        private bool _loading;

        public MainWindow()
        {
            Dialogs.Prepare(this, "KUDUCOPY");
            Width = 1240;
            Height = 800;
            MinWidth = 1000;
            MinHeight = 640;
            if (SystemParameters.WorkArea.Height < 840) WindowState = WindowState.Maximized;
            AllowDrop = true;

            Draft = new JobSpec
            {
                Destination = Settings.LastDestination,
                Conflict = Settings.Conflict == Conflict.Decide ? Conflict.Ask : Settings.Conflict,
                Verify = Settings.Verify,
                Hash = Settings.Hash,
                KeepGoing = Settings.KeepGoing,
                Advanced = Settings.Advanced,
                Switches = Settings.Switches,
                ExtraOptions = Settings.ExtraOptions
            };

            _start = Ui.Button("Start copying", "IconCopy", "PrimaryButton", (s, e) => Start());
            _start.MinHeight = 42;
            _start.Padding = new Thickness(28, 0, 28, 0);
            _start.FontSize = 14;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(BuildToolbar().At(0));
            root.Children.Add(_body.At(1));
            root.Children.Add(BuildActionBar().Margin(16, 12, 16, 16).At(2));
            root.Children.Add(BuildStatusBar().At(3));
            Content = root;

            _standardView = BuildStandard();
            _advancedView = BuildAdvanced();

            LoadDraft();
            HookEvents();
            Loaded += (s, e) =>
            {
                DetectRobocopy();
                ShowMode();
                CheckForUpdatesQuietly();
            };
            Closing += (s, e) => _counting?.Cancel();
        }

        public JobSpec Draft { get; private set; }

        internal void SelectAdvanced(bool advanced) => (advanced ? _advanced : _standard).IsChecked = true;

        private static RadioButton Seg(string text) =>
            new RadioButton { Content = text, MinHeight = 36, Padding = new Thickness(20, 0, 20, 0), FontSize = 13 }.Styled("SegmentRadio");

        // ================================================================== layout

        private UIElement BuildToolbar()
        {
            _copy.GroupName = "mode";
            _move.GroupName = "mode";
            _mirror.GroupName = "mode";
            _mirror.ToolTip = "Make the destination folder an exact copy of the source folder. Files that are only in the destination are deleted.";
            _standard.GroupName = "level";
            _advanced.GroupName = "level";

            var grid = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            grid.Margin = new Thickness(16, 12, 16, 12);
            grid.Children.Add(Segments(_copy, _move, _mirror).At(0, 0));
            grid.Children.Add(Ui.Subtle("files and folders", 13, false).Margin(12, 0, 0, 0).At(0, 1));
            grid.Children.Add(Segments(_standard, _advanced).At(0, 3));
            grid.Children.Add(Ui.Divider(true).Margin(12, 6, 8, 6).At(0, 4));
            grid.Children.Add(Ui.Button("Compare", "IconCompare", "GhostButton", (s, e) => ShowCompare())
                .Tip("Compare two folders and see which files are only on one side, newer or different").At(0, 5));
            grid.Children.Add(Ui.Button("Favourites", "IconStar", "GhostButton", (s, e) => ShowFavourites()).At(0, 6));
            grid.Children.Add(Ui.Button("History", "IconHistory", "GhostButton", (s, e) => new HistoryWindow(this).Show()).At(0, 7));
            grid.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this, () => _robocopyVersion).ShowDialog()).At(0, 8));
            return grid;
        }

        /// <summary>Two or more segment buttons in a rounded box.</summary>
        internal static Border Segments(params RadioButton[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < buttons.Length; i++)
            {
                buttons[i].BorderThickness = i == buttons.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 1, 0);
                panel.Children.Add(buttons[i]);
            }
            var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel, HorizontalAlignment = HorizontalAlignment.Left }
                .Res(Border.BorderBrushProperty, "InputBorder");
            border.SizeChanged += (s, e) =>
                panel.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, border.ActualWidth - 2), Math.Max(0, border.ActualHeight - 2)), 5, 5);
            return border;
        }

        /// <summary>Equal-width segments that fill the available width.</summary>
        internal static Border FillSegments(List<RadioButton> into, string group, params string[] labels)
        {
            var grid = new UniformGrid { Rows = 1 };
            for (var i = 0; i < labels.Length; i++)
            {
                var rb = new RadioButton { Content = new TextBlock { Text = labels[i], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, GroupName = group }
                    .Styled("SegmentRadio");
                rb.Padding = new Thickness(4, 6, 4, 6);
                rb.BorderThickness = i == labels.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 1, 0);
                into.Add(rb);
                grid.Children.Add(rb);
            }
            var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = grid }.Res(Border.BorderBrushProperty, "InputBorder");
            border.SizeChanged += (s, e) =>
                grid.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, border.ActualWidth - 2), Math.Max(0, border.ActualHeight - 2)), 5, 5);
            return border;
        }

        private FrameworkElement BuildActionBar()
        {
            var g = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            g.Children.Add(Ui.Column(_summary, _summaryNote.Margin(0, 2, 0, 0)).At(0, 0));
            var preview = Ui.Button("Preview", "IconList", click: (s, e) => Preview()).Tip("See what will be copied, replaced, skipped or deleted, before anything happens");
            preview.MinHeight = 42;
            g.Children.Add(preview.Margin(0, 0, 8, 0).At(0, 1));
            g.Children.Add(_start.At(0, 2));
            return Ui.Card(g, 14);
        }

        private UIElement BuildStatusBar()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            g.Children.Add(Ui.Dot().Margin(0, 0, 10, 0).At(0, 0));
            g.Children.Add(_status.At(0, 1));
            g.Children.Add(_statusRight.At(0, 2));
            return new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 7, 16, 7), Child = g }
                .Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceAltBg");
        }

        // ------------------------------------------------------------------ Standard

        private UIElement BuildStandard()
        {
            var grid = Ui.Columns(Ui.Star(), Ui.Px(12), Ui.Px(470));
            grid.Children.Add(BuildSourcesCard().At(0, 0));
            var right = new StackPanel();
            right.Children.Add(BuildDestinationCard());
            right.Children.Add(BuildOptionsCard().Margin(0, 12, 0, 0));
            grid.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.At(0, 2));
            return grid;
        }

        private UIElement BuildSourcesCard()
        {
            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            header.Children.Add(Ui.Heading("From", 14).At(0, 0));
            header.Children.Add(_sourceTotals.Margin(10, 0, 0, 0).At(0, 1));
            header.Children.Add(Ui.Small(Ui.Button("Add files", "IconPlus", click: (s, e) => AddSources(Dialogs.PickFiles(this, "Choose files to copy")))).At(0, 2));
            header.Children.Add(Ui.Small(Ui.Button("Add folder", "IconFolder", click: (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose a folder to copy");
                if (folder != null) AddSources(new[] { folder });
            })).Margin(6, 0, 0, 0).At(0, 3));

            var drop = new Border
            {
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(14, 12, 14, 14),
                Child = DropHint()
            }.Res(Border.BorderBrushProperty, "InputBorder");
            drop.BorderBrush = null;
            drop.Res(Border.BorderBrushProperty, "InputBorder");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(header.Margin(14, 12, 14, 10).At(0));
            grid.Children.Add(Ui.Divider().At(1));
            var list = new DockPanel();
            DockPanel.SetDock(drop, Dock.Bottom);
            list.Children.Add(drop);
            list.Children.Add(_sources);
            grid.Children.Add(list.At(2));
            var card = Ui.Card(grid, 0);
            card.ClipToBounds = true;
            return card;
        }

        private static UIElement DropHint()
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12.5 };
            t.Inlines.Add(new Run("Drop files or folders here, or press "));
            t.Inlines.Add(new Run("Ctrl+V") { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new Run(" to paste files you copied in File Explorer"));
            return t.Res(TextBlock.ForegroundProperty, "SubtleText");
        }

        private UIElement BuildDestinationCard()
        {
            _dest.Res(Control.FontFamilyProperty, "MonoFont");
            var row = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            row.Children.Add(_dest.At(0, 0));
            row.Children.Add(Ui.Button("Browse…", "IconFolder", click: (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose where to copy to", _dest.Text);
                if (folder != null) _dest.Text = folder;
            }).Margin(8, 0, 0, 0).At(0, 1));
            row.Children.Add(Ui.Button(string.Empty, "IconStar", "GhostButton", (s, e) => AddFavourite(_dest.Text)).Tip("Add this folder to favourites").At(0, 2));

            var driveRow = Ui.Columns(Ui.Auto, Ui.Star());
            driveRow.Children.Add(Ui.Icon("IconDrive", 18).Margin(0, 0, 10, 0).At(0, 0));
            var top = Ui.Columns(Ui.Star(), Ui.Auto);
            top.Children.Add(_driveName.At(0, 0));
            top.Children.Add(_driveFree.At(0, 1));
            driveRow.Children.Add(Ui.Column(top, _driveBar.Margin(0, 5, 0, 0), _driveAfter.Margin(0, 4, 0, 0)).At(0, 1));
            _driveBox.Children.Add(driveRow);

            return Ui.Card(Ui.Column(Ui.Heading("To", 14).Margin(0, 0, 0, 10), row, _favourites, _driveBox.Margin(0, 14, 0, 0)), 16);
        }

        private FrameworkElement BuildOptionsCard()
        {
            foreach (var h in new[] { HashKind.Md5, HashKind.Sha1, HashKind.Sha256, HashKind.Crc32 })
                _hash.Items.Add(new ComboBoxItem { Content = Checksums.Name(h), Tag = h });

            var verifyRow = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            verifyRow.Children.Add(_verify.At(0, 0));
            verifyRow.Children.Add(_hash.At(0, 2));

            return Ui.Card(Ui.Column(
                Ui.Heading("Options", 14).Margin(0, 0, 0, 10),
                Ui.Subtle("If a file already exists", 12, false).Margin(0, 0, 0, 6),
                _conflictBox = FillSegments(_conflict, "conflict", "Ask me", "Overwrite", "Replace if newer", "Skip", "Keep both"),
                _modeNote.Margin(0, 6, 0, 0),
                verifyRow.Margin(0, 14, 0, 0),
                _verifyHelp.Margin(46, 0, 0, 0),
                _keepGoing.Margin(0, 10, 0, 0),
                Ui.Subtle("Tries 3 times, then skips the file and lists it at the end so you can retry. When off, the job stops at the first problem.", 11.5).Margin(46, 0, 0, 0),
                Ui.Subtle("When finished", 12, false).Margin(0, 14, 0, 6),
                FillSegments(_after, "after", "Do nothing", "Close", "Play a sound", "Sleep", "Shut down")), 16);
        }

        // ------------------------------------------------------------------ Advanced

        private UIElement BuildAdvanced()
        {
            var grid = Ui.Columns(Ui.Px(230), Ui.Px(12), Ui.Star(), Ui.Px(12), Ui.Px(400));

            var navCard = Ui.Card(Ui.Column(Ui.Heading("Robocopy options", 13).Margin(6, 0, 0, 8), _nav), 10);
            grid.Children.Add(navCard.At(0, 0));

            var head = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            head.Children.Add(_groupTitle.At(0, 0));
            head.Children.Add(_groupHint.Margin(10, 0, 0, 0).At(0, 1));
            var searchBox = new Grid();
            searchBox.Children.Add(_search);
            var hint = Ui.Subtle("Search all options…", 12, false).Margin(8, 0, 0, 0);
            hint.IsHitTestVisible = true;
            hint.IsHitTestVisible = false;
            searchBox.Children.Add(hint);
            _search.TextChanged += (s, e) =>
            {
                hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                FillOptions();
            };
            head.Children.Add(searchBox.At(0, 2));
            var listGrid = new Grid();
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition());
            listGrid.Children.Add(head.Margin(14, 10, 14, 10).At(0));
            listGrid.Children.Add(Ui.Divider().At(1));
            listGrid.Children.Add(new ScrollViewer { Content = _optionList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.At(2));
            var listCard = Ui.Card(listGrid, 0);
            listCard.ClipToBounds = true;
            grid.Children.Add(listCard.At(0, 2));

            // Right column
            var right = new StackPanel();
            var what = Ui.Columns(Ui.Star(), Ui.Auto);
            what.Children.Add(_advWhat.At(0, 0));
            what.Children.Add(Ui.Link("Change", (s, e) => _standard.IsChecked = true).Margin(10, 0, 0, 0).At(0, 1));
            right.Children.Add(Ui.Card(Ui.Column(what, Ui.Subtle("Files, folders and destination are chosen in Standard mode. Verify and When finished apply here too.", 11.5)
                .Margin(0, 4, 0, 0)), 14));

            var cmdHead = Ui.Columns(Ui.Star(), Ui.Auto);
            cmdHead.Children.Add(Ui.Heading("Robocopy command", 13).At(0, 0));
            cmdHead.Children.Add(Ui.Small(Ui.Button("Copy", "IconCopy", click: (s, e) => CopyCommand())).At(0, 1));
            _command.Res(TextBlock.FontFamilyProperty, "MonoFont");
            var cmdBox = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Child = _command }
                .Res(Border.BackgroundProperty, "MonoBg").Res(Border.BorderBrushProperty, "BorderBrush");
            right.Children.Add(Ui.Card(Ui.Column(cmdHead, cmdBox.Margin(0, 10, 0, 0), _commandNote.Margin(0, 8, 0, 0), _advErrors.Margin(0, 6, 0, 0)), 14).Margin(0, 12, 0, 0));

            _extra.Res(Control.FontFamilyProperty, "MonoFont");
            right.Children.Add(Ui.Card(Ui.Column(Ui.Heading("Extra options", 13).Margin(0, 0, 0, 8), _extra,
                Ui.Subtle("Anything typed here is added to the command as it is, e.g. /XA:H /MAXAGE:30", 11.5).Margin(0, 6, 0, 0)), 14).Margin(0, 12, 0, 0));

            right.Children.Add(Ui.Card(Ui.Column(Ui.Heading("Job files", 13).Margin(0, 0, 0, 8),
                Ui.Row(Ui.Small(Ui.Button("Load .rcj…", "IconFile", click: (s, e) => LoadJobFile())),
                    Ui.Small(Ui.Button("Save as .rcj…", "IconSave", click: (s, e) => SaveJobFile())).Margin(8, 0, 0, 0)),
                Ui.Subtle("Robocopy job files can also be run from a script or Task Scheduler: robocopy /JOB:file", 11.5).Margin(0, 6, 0, 0),
                Ui.Link("Reset to the default options", (s, e) => ResetAdvanced()).Margin(0, 6, 0, 0)), 14).Margin(0, 12, 0, 0));

            grid.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.At(0, 4));
            return grid;
        }

        private void BuildNav()
        {
            _nav.Children.Clear();
            foreach (var g in RoboOptions.Groups)
            {
                var count = RoboOptions.All.Count(o => o.Group == g && IsSupported(o));
                var on = Draft.Switches.Count(s => RoboOptions.Find(s.Switch)?.Group == g);
                var row = Ui.Columns(Ui.Star(), Ui.Auto);
                row.Children.Add(Ui.Text(g, 13).At(0, 0));
                row.Children.Add(Ui.Subtle(on > 0 ? $"{on} on · {count}" : count.ToString(), 11.5, false).At(0, 1));
                var rb = new RadioButton { Content = row, GroupName = "nav", IsChecked = g == _group }.Styled("PresetRadio");
                var group = g;
                rb.Checked += (s, e) =>
                {
                    _group = group;
                    _search.Text = string.Empty;
                    FillOptions();
                };
                _nav.Children.Add(rb);
            }
            _nav.Children.Add(Ui.Subtle(string.IsNullOrEmpty(_robocopyVersion)
                ? $"{RoboOptions.All.Count(IsSupported)} options available"
                : $"Robocopy {_robocopyVersion} · {RoboOptions.All.Count(IsSupported)} options available on this PC", 11.5).Margin(8, 12, 8, 0));
        }

        private bool IsSupported(OptionDef o) => _supported == null || _supported.Contains(o.Switch);

        private void FillOptions()
        {
            _optionList.Children.Clear();
            var search = _search.Text.Trim();
            IEnumerable<OptionDef> options = RoboOptions.All.Where(IsSupported);
            if (search.Length > 0)
            {
                options = options.Where(o => o.Switch.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             o.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                _groupTitle.Text = "Search";
                _groupHint.Text = "Options matching \"" + search + "\"";
            }
            else
            {
                options = options.Where(o => o.Group == _group);
                _groupTitle.Text = _group;
                _groupHint.Text = GroupHint(_group);
            }

            foreach (var o in options) _optionList.Children.Add(OptionRow(o));
            if (_optionList.Children.Count == 0)
                _optionList.Children.Add(Ui.Subtle("No options match.", 13).Margin(16, 16, 16, 16));
        }

        private static string GroupHint(string g)
        {
            switch (g)
            {
                case RoboOptions.GroupCopy: return "How files and folders are copied";
                case RoboOptions.GroupSelect: return "Which files are copied";
                case RoboOptions.GroupRetry: return "What happens when a file can't be copied";
                case RoboOptions.GroupSpeed: return "Threads and bandwidth";
                case RoboOptions.GroupLog: return "Robocopy's own log file";
                default: return "Keep copying as things change";
            }
        }

        private UIElement OptionRow(OptionDef o)
        {
            var current = Draft.Switches.FirstOrDefault(s => string.Equals(s.Switch, o.Switch, StringComparison.OrdinalIgnoreCase));
            var box = new CheckBox { IsChecked = current != null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            var label = Ui.Mono(o.Label, 12.5, current != null ? "LinkText" : "Text");
            label.FontWeight = FontWeights.SemiBold;
            var desc = Ui.Text(o.Description, 12.5, wrap: true);
            var error = Ui.Text(string.Empty, 11.5, "BadText", wrap: true);
            error.Visibility = Visibility.Collapsed;

            var extras = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBox value = null;
            if (o.Kind != OptionKind.Flag)
            {
                value = new TextBox
                {
                    Text = current?.Value ?? string.Empty,
                    Width = o.Kind == OptionKind.List || o.Kind == OptionKind.Text ? 170 : 80,
                    MinHeight = 28,
                    FontSize = 12,
                    ToolTip = o.Kind == OptionKind.Letters || o.Kind == OptionKind.Attributes ? o.LetterHelp : "Default: " + (o.Default.Length == 0 ? "none" : o.Default)
                };
                value.Res(Control.FontFamilyProperty, "MonoFont");
                extras.Children.Add(value);
            }
            if (o.NeedsAdmin) extras.Children.Add(Ui.Chip("Needs admin", Tone.Warn, "IconShield").Margin(8, 0, 0, 0));
            if (o.Deletes) extras.Children.Add(Ui.Chip("Deletes files", Tone.Bad, "IconWarn").Margin(8, 0, 0, 0));

            var descCol = Ui.Column(desc, error);
            if ((o.Kind == OptionKind.Letters || o.Kind == OptionKind.Attributes) && !string.IsNullOrEmpty(o.LetterHelp))
                descCol.Children.Insert(1, Ui.Subtle(o.LetterHelp, 11.5));
            if (o.Kind == OptionKind.Number || o.Kind == OptionKind.Text)
                descCol.Children.Insert(1, Ui.Subtle("Empty uses " + (o.Default.Length == 0 ? "Robocopy's default" : o.Default), 11.5));

            var g = Ui.Columns(Ui.Auto, Ui.Px(110), Ui.Star(), Ui.Auto);
            g.Children.Add(box.Margin(0, 0, 10, 0).At(0, 0));
            g.Children.Add(label.At(0, 1));
            g.Children.Add(descCol.Margin(0, 0, 10, 0).At(0, 2));
            g.Children.Add(extras.At(0, 3));
            g.ToolTip = o.Warning;

            void Apply()
            {
                if (_loading) return;
                var on = box.IsChecked == true;
                label.Res(TextBlock.ForegroundProperty, on ? "LinkText" : "Text");
                Draft.Switches.RemoveAll(s => string.Equals(s.Switch, o.Switch, StringComparison.OrdinalIgnoreCase));
                if (on)
                {
                    var sv = new SwitchValue(o.Switch, value?.Text ?? string.Empty);
                    // Keep options in catalogue order so the command reads naturally.
                    var index = Draft.Switches.FindIndex(s => Order(s.Switch) > Order(o.Switch));
                    if (index < 0) Draft.Switches.Add(sv);
                    else Draft.Switches.Insert(index, sv);
                    var problem = RoboOptions.Validate(sv);
                    error.Text = problem ?? o.Warning ?? string.Empty;
                    error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible;
                    error.Res(TextBlock.ForegroundProperty, problem != null || o.Deletes ? "BadText" : "WarnText");
                }
                else
                {
                    error.Visibility = Visibility.Collapsed;
                }
                Settings.Switches = Draft.Switches;
                RefreshAdvanced();
            }

            box.Click += (s, e) => Apply();
            if (value != null)
            {
                value.TextChanged += (s, e) =>
                {
                    if (box.IsChecked != true && value.IsKeyboardFocusWithin && value.Text.Length > 0) box.IsChecked = true;
                    Apply();
                };
            }
            if (current != null)
            {
                var problem = RoboOptions.Validate(current);
                error.Text = problem ?? o.Warning ?? string.Empty;
                error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible;
                error.Res(TextBlock.ForegroundProperty, problem != null || o.Deletes ? "BadText" : "WarnText");
            }

            return new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 8, 14, 8) }.Res(Border.BorderBrushProperty, "BorderBrush");
        }

        private static int Order(string sw)
        {
            for (var i = 0; i < RoboOptions.All.Count; i++)
                if (string.Equals(RoboOptions.All[i].Switch, sw, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        // ================================================================== state

        private void LoadDraft()
        {
            _loading = true;
            (Draft.Mode == CopyMode.Move ? _move : Draft.Mode == CopyMode.Mirror ? _mirror : _copy).IsChecked = true;
            (Draft.Advanced ? _advanced : _standard).IsChecked = true;
            _dest.Text = Draft.Destination ?? string.Empty;
            var c = Draft.Conflict;
            _conflict[c == Conflict.Overwrite ? 1 : c == Conflict.ReplaceIfNewer ? 2 : c == Conflict.Skip ? 3 : c == Conflict.KeepBoth ? 4 : 0].IsChecked = true;
            _verify.IsChecked = Draft.Verify;
            _hash.SelectedItem = _hash.Items.Cast<ComboBoxItem>().First(i => (HashKind)i.Tag == Draft.Hash);
            _keepGoing.IsChecked = Draft.KeepGoing;
            _after[(int)Draft.After].IsChecked = true;
            _extra.Text = Draft.ExtraOptions ?? string.Empty;
            _loading = false;
            RefreshSources();
            RefreshFavourites();
            RefreshDestination();
            RefreshOptions();
        }

        private void HookEvents()
        {
            _copy.Checked += (s, e) => { Draft.Mode = CopyMode.Copy; RefreshMode(); };
            _move.Checked += (s, e) => { Draft.Mode = CopyMode.Move; RefreshMode(); };
            _mirror.Checked += (s, e) => { Draft.Mode = CopyMode.Mirror; RefreshMode(); };
            _standard.Checked += (s, e) => { if (!_loading) { Draft.Advanced = false; Settings.Advanced = false; ShowMode(); } };
            _advanced.Checked += (s, e) => { if (!_loading) { Draft.Advanced = true; Settings.Advanced = true; ShowMode(); } };
            _dest.TextChanged += (s, e) =>
            {
                Draft.Destination = _dest.Text.Trim();
                RefreshDestination();
                RefreshSummary();
            };
            for (var i = 0; i < _conflict.Count; i++)
            {
                var policy = new[] { Conflict.Ask, Conflict.Overwrite, Conflict.ReplaceIfNewer, Conflict.Skip, Conflict.KeepBoth }[i];
                _conflict[i].Checked += (s, e) =>
                {
                    if (_loading) return;
                    Draft.Conflict = policy;
                    Settings.Conflict = policy;
                    RefreshSummary();
                };
            }
            _verify.Click += (s, e) =>
            {
                Draft.Verify = _verify.IsChecked == true;
                Settings.Verify = Draft.Verify;
                RefreshOptions();
            };
            _hash.SelectionChanged += (s, e) =>
            {
                if (_loading || !(_hash.SelectedItem is ComboBoxItem item)) return;
                Draft.Hash = (HashKind)item.Tag;
                Settings.Hash = Draft.Hash;
                RefreshOptions();
            };
            _keepGoing.Click += (s, e) =>
            {
                Draft.KeepGoing = _keepGoing.IsChecked == true;
                Settings.KeepGoing = Draft.KeepGoing;
                RefreshAdvanced();
            };
            for (var i = 0; i < _after.Count; i++)
            {
                var after = (AfterFinish)i;
                _after[i].Checked += (s, e) => Draft.After = after;
            }
            _extra.TextChanged += (s, e) =>
            {
                Draft.ExtraOptions = _extra.Text;
                Settings.ExtraOptions = _extra.Text;
                RefreshAdvanced();
            };

            Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddSources(files);
            };
            DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox))
                {
                    PasteSources();
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete && _sources.IsKeyboardFocusWithin && _sources.SelectedItem is ListBoxItem item)
                {
                    RemoveSource((string)item.Tag);
                    e.Handled = true;
                }
            };
        }

        private void ShowMode()
        {
            _body.Children.Clear();
            if (Draft.Advanced)
            {
                BuildNav();
                FillOptions();
                RefreshAdvanced();
                _body.Children.Add(_advancedView);
                _statusRight.Text = "Advanced mode · every option maps to a Robocopy switch";
            }
            else
            {
                _body.Children.Add(_standardView);
                _statusRight.Text = "Standard mode · switch to Advanced for every Robocopy option";
            }
            RefreshSummary();
        }

        private void RefreshOptions()
        {
            _hash.Visibility = Draft.Verify ? Visibility.Visible : Visibility.Collapsed;
            _verifyHelp.Text = Draft.Verify
                ? $"On. After copying, KUDUCOPY reads every file again and compares {Checksums.Name(Draft.Hash)} checksums. Slower, but proves each copy is identical."
                : "Off. When on, KUDUCOPY compares checksums after copying. Slower, but proves each copy is identical.";
        }

        // ------------------------------------------------------------------ sources

        public void AddSources(IEnumerable<string> paths)
        {
            var added = 0;
            foreach (var p in paths ?? Enumerable.Empty<string>())
            {
                string full;
                try { full = Planner.Full(p); }
                catch { continue; }
                if (!File.Exists(full) && !Directory.Exists(full)) continue;
                if (Draft.Sources.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
                Draft.Sources.Add(full);
                added++;
            }
            if (added > 0) RefreshSources();
        }

        public void SetMode(CopyMode mode) => (mode == CopyMode.Move ? _move : mode == CopyMode.Mirror ? _mirror : _copy).IsChecked = true;

        private void RefreshMode()
        {
            var mirror = Draft.Mode == CopyMode.Mirror;
            if (_conflictBox != null) _conflictBox.IsEnabled = !mirror;
            _modeNote.Text = mirror
                ? "Mirror replaces changed files and DELETES files that are only in the destination folder. You'll see a preview first."
                : string.Empty;
            _modeNote.Res(TextBlock.ForegroundProperty, mirror ? "BadText" : "SubtleText");
            _modeNote.Visibility = mirror ? Visibility.Visible : Visibility.Collapsed;
            RefreshAdvanced();
            RefreshSummary();
        }

        public void FocusDestination()
        {
            _standard.IsChecked = true;
            _dest.Focus();
            _dest.SelectAll();
        }

        private void PasteSources()
        {
            var files = ClipboardFiles.Get(out var cut);
            if (files.Count == 0)
            {
                SetStatus("Nothing to paste. Copy or cut files in File Explorer first.");
                return;
            }
            AddSources(files);
            if (cut) SetMode(CopyMode.Move);
            SetStatus($"Added {Format.Count(files.Count, "item")} from the clipboard" + (cut ? " (cut, so Move is selected)." : "."));
        }

        /// <summary>Opens Compare, starting from the first source folder and its copy in the destination when there is one.</summary>
        internal CompareWindow ShowCompare()
        {
            string left = null, right = null;
            try
            {
                left = Draft.Sources.FirstOrDefault(Directory.Exists);
                var dest = _dest.Text.Trim();
                if (left != null && dest.Length > 0)
                {
                    var copy = Path.Combine(Planner.Full(dest), Planner.DisplayName(left));
                    right = Directory.Exists(copy) ? copy : null;
                }
            }
            catch { /* start empty */ }
            var w = new CompareWindow(this, left, right);
            w.Show();
            return w;
        }

        private void RemoveSource(string path)
        {
            Draft.Sources.RemoveAll(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase));
            RefreshSources();
        }

        private void RefreshSources()
        {
            _sources.Items.Clear();
            foreach (var path in Draft.Sources)
            {
                var isFolder = Directory.Exists(path);
                var tile = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(6),
                    Child = Ui.Icon(isFolder ? "IconFolder" : "IconFile", 16),
                    Margin = new Thickness(0, 0, 12, 0)
                }.Res(Border.BackgroundProperty, "ChipBg");
                tile.Res(TextElement.ForegroundProperty, "ChipText");
                ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;

                var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
                g.Children.Add(tile.At(0, 0));
                g.Children.Add(Ui.Column(Ui.Text(Planner.DisplayName(path), 13, bold: true), Ui.Subtle(Path.GetDirectoryName(path) ?? path, 11.5, false)).At(0, 1));
                g.Children.Add(Ui.Subtle(SizeText(path), 12, false).Margin(8, 0, 8, 0).At(0, 2));
                var remove = Ui.Button(string.Empty, "IconClose", "GhostButton", (s, e) => RemoveSource(path)).Tip("Remove from the list");
                remove.MinHeight = 28;
                remove.Padding = new Thickness(6, 0, 6, 0);
                g.Children.Add(remove.At(0, 3));
                _sources.Items.Add(new ListBoxItem { Content = g, Tag = path, Padding = new Thickness(14, 8, 10, 8) });
            }
            CountSources();
            RefreshSummary();
        }

        private string SizeText(string path)
        {
            if (!_sizes.TryGetValue(path, out var s)) return Directory.Exists(path) ? "Counting…" : string.Empty;
            return Directory.Exists(path) ? $"{Format.Count(s.Files, "file")} · {Format.Size(s.Bytes)}" : Format.Size(s.Bytes);
        }

        private void CountSources()
        {
            _counting?.Cancel();
            var cts = new CancellationTokenSource();
            _counting = cts;
            var pending = Draft.Sources.Where(p => !_sizes.ContainsKey(p)).ToList();
            UpdateTotals();
            if (pending.Count == 0) return;

            Task.Run(() =>
            {
                foreach (var path in pending)
                {
                    if (cts.IsCancellationRequested) return;
                    var result = Count(path, cts.Token);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _sizes[path] = result;
                        if (cts.IsCancellationRequested) return;
                        foreach (ListBoxItem item in _sources.Items)
                        {
                            if (!string.Equals((string)item.Tag, path, StringComparison.OrdinalIgnoreCase)) continue;
                            var grid = (Grid)item.Content;
                            ((TextBlock)grid.Children[2]).Text = SizeText(path);
                        }
                        UpdateTotals();
                    }));
                }
            });
        }

        private static (int, long) Count(string path, CancellationToken cancel)
        {
            try
            {
                if (File.Exists(path)) return (1, new FileInfo(path).Length);
                var files = 0;
                long bytes = 0;
                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0 && !cancel.IsCancellationRequested)
                {
                    var dir = stack.Pop();
                    try
                    {
                        foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                        {
                            files++;
                            bytes += f.Length;
                        }
                        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                            if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName);
                    }
                    catch
                    {
                        // Unreadable folder: robocopy will report it.
                    }
                }
                return (files, bytes);
            }
            catch
            {
                return (0, 0);
            }
        }

        private void UpdateTotals()
        {
            var known = Draft.Sources.Where(p => _sizes.ContainsKey(p)).ToList();
            var files = known.Sum(p => _sizes[p].Files);
            var bytes = known.Sum(p => _sizes[p].Bytes);
            var counting = known.Count < Draft.Sources.Count;
            _sourceTotals.Text = Draft.Sources.Count == 0 ? string.Empty
                : $"{Format.Count(Draft.Sources.Count, "item")} · {Format.Count(files, "file")} · {Format.Size(bytes)}" + (counting ? " · counting…" : string.Empty);
            RefreshDestination();
            RefreshSummary();
        }

        private long TotalBytes => Draft.Sources.Where(p => _sizes.ContainsKey(p)).Sum(p => _sizes[p].Bytes);
        private int TotalFiles => Draft.Sources.Where(p => _sizes.ContainsKey(p)).Sum(p => _sizes[p].Files);

        // ------------------------------------------------------------------ destination

        private void RefreshFavourites()
        {
            _favourites.Children.Clear();
            var favs = Settings.Favourites;
            foreach (var f in favs)
            {
                var folder = f;
                var b = Ui.Button(Planner.DisplayName(folder) == folder ? folder : folder, "IconStar", "GhostButton", (s, e) => _dest.Text = folder);
                b.MinHeight = 28;
                b.FontSize = 12;
                b.Padding = new Thickness(8, 0, 10, 0);
                b.Res(Control.BackgroundProperty, "NeutralChipBg");
                b.Margin = new Thickness(0, 0, 6, 6);
                b.ToolTip = "Copy to " + folder + ". Right-click to remove.";
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "Remove from favourites" };
                remove.Click += (s, e) =>
                {
                    Settings.Favourites = Settings.Favourites.Where(x => !string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)).ToList();
                    ShellIntegration.Refresh();
                    RefreshFavourites();
                };
                menu.Items.Add(remove);
                b.ContextMenu = menu;
                _favourites.Children.Add(b);
            }
            if (favs.Count == 0)
                _favourites.Children.Add(Ui.Subtle("Tip: press ☆ to add this folder to your favourites. They also appear in File Explorer's right-click menu.", 11.5));
        }

        public void AddFavourite(string folder)
        {
            folder = (folder ?? string.Empty).Trim();
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                SetStatus("Choose an existing folder first, then add it to favourites.");
                return;
            }
            var favs = Settings.Favourites;
            if (!favs.Contains(folder, StringComparer.OrdinalIgnoreCase)) favs.Add(folder);
            Settings.Favourites = favs;
            ShellIntegration.Refresh();
            RefreshFavourites();
            SetStatus(folder + " added to favourites.");
        }

        public void RefreshFavouritesFromSettings() => RefreshFavourites();

        private void RefreshDestination()
        {
            var dest = Draft.Destination;
            _driveBox.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(dest)) return;
            try
            {
                var root = Path.GetPathRoot(Planner.Full(dest));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    _driveName.Text = "Network folder";
                    _driveFree.Text = string.Empty;
                    _driveBar.Visibility = Visibility.Collapsed;
                    _driveAfter.Text = "Free space can't be checked in advance for network folders.";
                    _driveBox.Visibility = Visibility.Visible;
                    return;
                }
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;
                var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                _driveName.Text = $"{label} ({root.TrimEnd('\\')})";
                _driveFree.Text = $"{Format.Size(drive.AvailableFreeSpace)} free of {Format.Size(drive.TotalSize)}";
                _driveBar.Visibility = Visibility.Visible;
                _driveBar.Value = drive.TotalSize == 0 ? 0 : 1 - (double)drive.AvailableFreeSpace / drive.TotalSize;
                var after = drive.AvailableFreeSpace - TotalBytes;
                _driveAfter.Text = Draft.Sources.Count == 0 ? string.Empty
                    : after >= 0 ? $"After copying: about {Format.Size(after)} free. Enough space ✓"
                    : $"Not enough space: about {Format.Size(-after)} more is needed.";
                _driveAfter.Res(TextBlock.ForegroundProperty, after >= 0 ? "SubtleText" : "BadText");
                _driveBox.Visibility = Visibility.Visible;
            }
            catch
            {
                // Not a valid path yet.
            }
        }

        // ------------------------------------------------------------------ advanced

        private void DetectRobocopy()
        {
            if (_supported != null) return;
            try
            {
                var psi = new ProcessStartInfo(CopyJob.RobocopyPath, "/?")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                };
                using (var p = Process.Start(psi))
                {
                    var help = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    _supported = RoboOptions.Supported(help);
                    _robocopyVersion = RoboOptions.Version(help);
                }
            }
            catch
            {
                _supported = RoboOptions.Supported(string.Empty);
            }
            SetStatus(File.Exists(CopyJob.RobocopyPath)
                ? "Ready · uses Windows' built-in Robocopy" + (string.IsNullOrEmpty(_robocopyVersion) ? string.Empty : " " + _robocopyVersion)
                : "Robocopy wasn't found on this PC. It's part of Windows; check " + Environment.SystemDirectory);
        }

        private RoboTask PreviewTask()
        {
            try
            {
                var tasks = Planner.Tasks(Draft);
                if (tasks.Count > 0) return tasks[0];
            }
            catch
            {
                // Fall back to placeholders.
            }
            return new RoboTask { SourceDir = "<source>", DestDir = string.IsNullOrWhiteSpace(Draft.Destination) ? "<destination>" : Draft.Destination };
        }

        private void RefreshAdvanced()
        {
            if (!Draft.Advanced) return;
            var task = PreviewTask();
            var args = Planner.Build(Draft, task, false, Conflict.Overwrite, Draft.Mode == CopyMode.Move && !Draft.Verify);
            if (_command == null) return;
            _command.Inlines.Clear();
            _command.Inlines.Add(new Run("robocopy ") { FontWeight = FontWeights.SemiBold }.Brush("LinkText"));
            foreach (var a in args)
            {
                var internalArg = RoboOptions.OutputSwitches.Contains(a, StringComparer.OrdinalIgnoreCase);
                _command.Inlines.Add(new Run(Args.Quote(a) + " ").Brush(internalArg ? "SubtleText" : "Text"));
            }
            int taskCount;
            try { taskCount = Planner.Tasks(Draft).Count; }
            catch { taskCount = 0; }
            _commandNote.Text = (taskCount > 1 ? $"One command per source ({taskCount} in total). " : string.Empty) +
                                "Grey options are added by KUDUCOPY to show progress. Options this PC's Robocopy doesn't support are hidden.";

            var problems = Draft.Switches.Select(RoboOptions.Validate).Where(p => p != null).ToList();
            _advErrors.Text = string.Join("\n", problems);
            _advErrors.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildNavCounts();
            RefreshSummary();
        }

        private void BuildNavCounts()
        {
            if (_nav.Children.Count == 0) return;
            for (var i = 0; i < RoboOptions.Groups.Length && i < _nav.Children.Count; i++)
            {
                if (!(_nav.Children[i] is RadioButton rb) || !(rb.Content is Grid row) || row.Children.Count < 2) continue;
                var g = RoboOptions.Groups[i];
                var count = RoboOptions.All.Count(o => o.Group == g && IsSupported(o));
                var on = Draft.Switches.Count(s => RoboOptions.Find(s.Switch)?.Group == g);
                ((TextBlock)row.Children[1]).Text = on > 0 ? $"{on} on · {count}" : count.ToString();
            }
        }

        private void CopyCommand()
        {
            try
            {
                var lines = Planner.Tasks(Draft).Select(t => Planner.Preview(Draft, t));
                Ui.Copy(string.Join(Environment.NewLine, lines));
            }
            catch
            {
                Ui.Copy("robocopy " + Args.Join(Planner.Build(Draft, PreviewTask(), false, Conflict.Overwrite, false)));
            }
            SetStatus("Robocopy command copied. Paste it into a terminal to run the same copy without KUDUCOPY.");
        }

        private void ResetAdvanced()
        {
            Draft.Switches = RoboOptions.Defaults();
            Draft.ExtraOptions = string.Empty;
            Settings.Switches = Draft.Switches;
            Settings.ExtraOptions = string.Empty;
            _extra.Text = string.Empty;
            BuildNav();
            FillOptions();
            RefreshAdvanced();
        }

        private void SaveJobFile()
        {
            var path = Dialogs.SaveFile(this, "Save a Robocopy job file", "KUDUCOPY job.rcj", "Robocopy job|*.rcj");
            if (path == null) return;
            try
            {
                var task = PreviewTask();
                if (task.SourceDir == "<source>")
                {
                    Dialogs.Error(this, "Add a folder to copy and choose a destination first. A job file contains them.");
                    return;
                }
                var args = Planner.Build(Draft, task, false, Conflict.Overwrite, Draft.Mode == CopyMode.Move && !Draft.Verify)
                    .Where(a => !RoboOptions.OutputSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
                args.Add("/SAVE:" + Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path)));
                args.Add("/QUIT");
                using (var p = Process.Start(new ProcessStartInfo(CopyJob.RobocopyPath, Args.Join(args)) { UseShellExecute = false, CreateNoWindow = true }))
                    p?.WaitForExit(10000);
                SetStatus(File.Exists(path) ? "Saved " + Path.GetFileName(path) : "Robocopy didn't create the job file.");
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "Couldn't save the job file:\n\n" + ex.Message);
            }
        }

        private void LoadJobFile()
        {
            var path = Dialogs.PickFile(this, "Load a Robocopy job file", "Robocopy job|*.rcj|All files|*.*");
            if (path == null) return;
            try
            {
                var job = JobFile.Parse(File.ReadAllLines(path));
                if (job.Source != null) { Draft.Sources = new List<string>(); AddSources(new[] { job.Source }); }
                if (job.Destination != null) _dest.Text = job.Destination;
                Draft.Switches = job.Switches;
                Draft.ExtraOptions = string.Join(" ", job.Unknown.Select(Args.Quote));
                _extra.Text = Draft.ExtraOptions;
                Settings.Switches = Draft.Switches;
                BuildNav();
                FillOptions();
                RefreshAdvanced();
                SetStatus($"Loaded {Path.GetFileName(path)} · {job.Switches.Count} options");
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "Couldn't read the job file:\n\n" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ summary and start

        private void RefreshSummary()
        {
            if (_summary == null) return;
            var count = Draft.Sources.Count;
            var dest = string.IsNullOrWhiteSpace(Draft.Destination) ? "…" : Draft.Destination;
            if (count == 0)
            {
                _summary.Text = "Add files or folders to " + Draft.Verb.ToLowerInvariant();
                _summaryNote.Text = "Drag them in, use Add files / Add folder, or paste with Ctrl+V.";
            }
            else if (Draft.Mode == CopyMode.Mirror)
            {
                var names = string.Join(", ", Draft.Sources.Select(Planner.DisplayName).Take(2)) + (count > 2 ? $" and {count - 2} more" : string.Empty);
                _summary.Text = $"Mirror {names} to {dest}";
                _summaryNote.Text = "The destination folder becomes an exact copy. Files only there are deleted. You'll see a preview first.";
            }
            else
            {
                var counted = Draft.Sources.All(p => _sizes.ContainsKey(p));
                _summary.Text = counted
                    ? $"{Draft.Verb} {Format.Count(TotalFiles, "file")} ({Format.Size(TotalBytes)}) to {dest}"
                    : $"{Draft.Verb} {Format.Count(count, "item")} to {dest}";
                var key = string.IsNullOrWhiteSpace(Draft.Destination) ? null : Planner.VolumeKey(Draft.Destination);
                var busy = key != null && JobQueue.IsBusy(key);
                _summaryNote.Text = (busy ? $"Another job is copying to {key}; this one will wait its turn. " : string.Empty) +
                                    (Draft.Advanced ? $"Advanced mode · {Format.Count(Draft.Switches.Count, "option")}" : string.Empty) +
                                    (Draft.Verify ? (Draft.Advanced ? " · " : string.Empty) + "verify on (" + Checksums.Name(Draft.Hash) + ")" : string.Empty);
            }
            _start.Content = Draft.Mode == CopyMode.Mirror
                ? Ui.Label("IconList", "Preview mirror…")
                : Ui.Label(Draft.Mode == CopyMode.Move ? "IconMove" : "IconCopy", Draft.Mode == CopyMode.Move ? "Start moving" : "Start copying");
        }

        private void Start()
        {
            if (Draft.Mode == CopyMode.Mirror)
            {
                // Mirror can delete files: always show what will happen first.
                Preview();
                return;
            }
            var spec = Prepare(false);
            if (spec == null) return;
            Settings.LastDestination = spec.Destination;
            if (App.StartJob(spec, this)) Started(spec);
        }

        private void Preview()
        {
            var spec = Prepare(true);
            if (spec == null) return;
            Settings.LastDestination = spec.Destination;
            new PreviewWindow(spec, this, () => Started(spec)).Show();
        }

        private void Started(JobSpec spec)
        {
            Draft.Sources = new List<string>();
            _sizes.Clear();
            RefreshSources();
            SetStatus($"Started: {spec.Verb.ToLowerInvariant()} to {spec.Destination}. You can add another job now.");
        }

        /// <summary>Checks the choices and returns the job to run, or null (after telling the user why).</summary>
        private JobSpec Prepare(bool preview)
        {
            if (Draft.Sources.Count == 0)
            {
                Dialogs.Info(this, "Add some files or folders to copy first. Drag them in, use Add files / Add folder, or paste with Ctrl+V.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(Draft.Destination))
            {
                Dialogs.Info(this, "Choose where to copy to.");
                FocusDestination();
                return null;
            }

            var spec = Draft.Clone();
            if (spec.Mode == CopyMode.Mirror && spec.Sources.Any(File.Exists))
            {
                Dialogs.Info(this, "Mirror works with folders. Remove the files from the list, or choose Copy.");
                return null;
            }
            try
            {
                Planner.Tasks(spec); // friendly errors (into itself, missing files) before anything starts
                if (!preview) Directory.CreateDirectory(Planner.Full(spec.Destination));
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, ex is InvalidOperationException ? ex.Message : "That destination can't be used:\n\n" + ex.Message);
                return null;
            }

            if (spec.Advanced)
            {
                var problems = spec.Switches.Select(RoboOptions.Validate).Where(p => p != null).ToList();
                if (problems.Count > 0)
                {
                    Dialogs.Error(this, "Fix these options first:\n\n" + string.Join("\n", problems));
                    return null;
                }
                if (!preview)
                {
                    var admin = spec.Switches.Where(s => RoboOptions.Find(s.Switch)?.NeedsAdmin == true).Select(s => s.Switch).ToList();
                    if (spec.Switches.Any(s => s.Switch == "/COPY" && (s.Value ?? string.Empty).ToUpperInvariant().IndexOfAny(new[] { 'O', 'U' }) >= 0)) admin.Add("/COPY:O or U");
                    if (admin.Count > 0 && !Admin.IsAdmin)
                    {
                        if (Dialogs.Confirm(this, $"{string.Join(", ", admin)} need{(admin.Count == 1 ? "s" : "")} administrator rights.\n\nRestart KUDUCOPY as administrator and start this job?"))
                            RestartAsAdmin(spec);
                        return null;
                    }
                    var deleting = spec.Switches.Where(s => RoboOptions.Find(s.Switch)?.Deletes == true).Select(s => s.Switch).ToList();
                    if (deleting.Count > 0 &&
                        !Dialogs.Confirm(this, $"{string.Join(" and ", deleting)} will DELETE files and folders in {spec.Destination} that aren't in the source.\n\n" +
                                               "Use Preview to see which ones. Start anyway?"))
                        return null;
                }
            }
            return spec;
        }

        private void RestartAsAdmin(JobSpec spec)
        {
            try
            {
                var file = Path.Combine(AppPaths.Temp, "restart-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                Json.Write(file, spec);
                if (Admin.RestartElevated(new[] { "--job", file })) Application.Current.Shutdown();
                else SetStatus("Restart as administrator was cancelled.");
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "Couldn't restart as administrator:\n\n" + ex.Message);
            }
        }

        private void ShowFavourites()
        {
            new FavouritesWindow(this).ShowDialog();
            RefreshFavourites();
        }

        public void SetStatus(string text) => _status.Text = text;

        private async void CheckForUpdatesQuietly()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                SetStatus($"KUDUCOPY {info.Latest.ToString(3)} is available. Open Settings → About to download it.");
            }
            catch
            {
                // Offline: try again next time.
            }
        }
    }

    internal static class RunExtensions
    {
        public static Run Brush(this Run run, string key)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, key);
            return run;
        }
    }
}
