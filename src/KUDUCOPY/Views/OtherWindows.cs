using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    /// <summary>Past jobs: see what failed, retry, run again, open the log.</summary>
    public sealed class HistoryWindow : Window
    {
        private readonly ListBox _list = new ListBox().Styled("TableList");
        private readonly StackPanel _detail = new StackPanel();
        private readonly TextBlock _count = Ui.Subtle(string.Empty, 12.5, false);
        private readonly TextBox _search = new TextBox { Width = 240, MinHeight = 32, FontSize = 12 };
        private List<JobRecord> _records = new List<JobRecord>();
        private static readonly GridLength[] Widths = { Ui.Px(150), Ui.Px(70), Ui.Star(), Ui.Star(), Ui.Px(150), Ui.Px(140) };

        public HistoryWindow(Window owner)
        {
            Width = 1000;
            Height = 620;
            MinWidth = 760;
            MinHeight = 420;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "History · KUDUCOPY", owner);

            var searchBox = new Grid();
            searchBox.Children.Add(_search);
            var hint = Ui.Subtle("Search…", 12, false).Margin(8, 0, 0, 0);
            hint.IsHitTestVisible = false;
            searchBox.Children.Add(hint);
            _search.TextChanged += (s, e) =>
            {
                hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Fill();
            };

            var head = Ui.Columns(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.Column(Ui.Heading("History", 20), _count).At(0, 0));
            head.Children.Add(searchBox.At(0, 1));

            var cols = Ui.Columns(Widths);
            var titles = new[] { "When", "", "From", "To", "Size", "Result" };
            for (var i = 0; i < titles.Length; i++) cols.Children.Add(Ui.Text(titles[i], 12, "SubtleText", bold: true).Margin(i == 0 ? 14 : 0, 8, 8, 8).At(0, i));
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition());
            table.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = cols }.Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            table.Children.Add(_list.At(1));
            var card = Ui.Card(table, 0);
            card.ClipToBounds = true;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(head.Margin(0, 0, 0, 14).At(0));
            root.Children.Add(card.At(1));
            root.Children.Add(_detail.Margin(0, 12, 0, 0).At(2));
            Content = root;

            _list.SelectionChanged += (s, e) => ShowDetail((_list.SelectedItem as ListBoxItem)?.Tag as JobRecord);
            _list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete && (_list.SelectedItem as ListBoxItem)?.Tag is JobRecord r) Delete(r);
            };
            _records = History.List();
            Fill();
        }

        private void Fill()
        {
            _list.Items.Clear();
            var q = _search.Text.Trim();
            var records = _records.Where(r => q.Length == 0 || r.From.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || r.To.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var r in records)
            {
                var g = Ui.Columns(Widths);
                g.Children.Add(Ui.Text(r.WhenText, 12.5).Margin(14, 0, 0, 0).At(0, 0));
                g.Children.Add(Ui.Chip(r.Mode == CopyMode.Move ? "Move" : r.Mode == CopyMode.Mirror ? "Mirror" : "Copy", Tone.Neutral).At(0, 1));
                g.Children.Add(Ui.Text(r.From, 12.5).Margin(0, 0, 8, 0).At(0, 2));
                g.Children.Add(Ui.Mono(r.To, 12).Margin(0, 0, 8, 0).At(0, 3));
                g.Children.Add(Ui.Subtle($"{Format.Count(r.Files, "file")} · {Format.Size(r.Bytes)}", 12.5, false).At(0, 4));
                var tone = r.Result == JobResult.Done ? Tone.Ok : r.Result == JobResult.Cancelled ? Tone.Neutral : r.Result == JobResult.Failed ? Tone.Bad : Tone.Warn;
                g.Children.Add(Ui.Chip(r.ResultText, tone).At(0, 5));
                _list.Items.Add(new ListBoxItem { Content = g, Tag = r, MinHeight = 38 });
            }
            _count.Text = _records.Count == 0 ? "No jobs yet. Every finished job is saved here." : $"{Format.Count(_records.Count, "job")} · kept on this computer only";
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            else ShowDetail(null);
        }

        private void ShowDetail(JobRecord r)
        {
            _detail.Children.Clear();
            if (r == null) return;
            var verb = r.Mode == CopyMode.Move ? "Move" : r.Mode == CopyMode.Mirror ? "Mirror" : "Copy";
            var head = Ui.Row(Ui.Text($"{verb} · {r.From} → {r.To}", 13.5, bold: true),
                Ui.Subtle($"   {r.WhenText} · {r.Copied:N0} of {Format.Count(r.Files, "file")} {(r.Mode == CopyMode.Move ? "moved" : "copied")}" +
                          (r.Deleted > 0 ? $" · {r.Deleted:N0} deleted" : string.Empty) +
                          (string.IsNullOrEmpty(r.Message) || r.Message == "Done" ? string.Empty : " · " + r.Message), 12, false));
            var body = Ui.Column(head);
            foreach (var f in r.Failed.Take(4))
            {
                var x = Ui.Icon("IconClose", 13).Margin(0, 0, 6, 0);
                x.Res(TextElement.ForegroundProperty, "BadText");
                body.Children.Add(Ui.Row(x, Ui.Text(Path.GetFileName(f.Source) + " · " + f.Reason, 12.5)).Margin(0, 6, 0, 0));
            }
            if (r.Failed.Count > 4) body.Children.Add(Ui.Subtle($"and {r.Failed.Count - 4:N0} more", 12).Margin(19, 4, 0, 0));

            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (r.Failed.Count > 0 && r.Spec != null)
                buttons.Children.Add(Ui.Small(Ui.Button($"Retry {Format.Count(r.Failed.Count, "failed file")}", "IconRefresh", "PrimaryButton",
                    (s, e) => App.StartJob(Retry.For(r.Spec, r.Failed), this))).Margin(0, 0, 8, 0));
            if (r.Spec != null)
                buttons.Children.Add(Ui.Small(Ui.Button("Run this job again", "IconPlay", click: (s, e) => App.StartJob(r.Spec.Clone(), this))).Margin(0, 0, 8, 0));
            buttons.Children.Add(Ui.Small(Ui.Button("Open destination", "IconFolder", click: (s, e) => AppInfo.OpenFolder(r.To))).Margin(0, 0, 8, 0));
            if (File.Exists(r.LogPath))
                buttons.Children.Add(Ui.Small(Ui.Button("View Robocopy log", "IconLog", click: (s, e) => AppInfo.OpenFile(r.LogPath))).Margin(0, 0, 8, 0));
            buttons.Children.Add(Ui.Small(Ui.Button("Delete", "IconTrash", "GhostButton", (s, e) => Delete(r))));
            body.Children.Add(buttons);
            _detail.Children.Add(Ui.Card(body, 14));
        }

        private void Delete(JobRecord r)
        {
            if (!Dialogs.Confirm(this, "Remove this job from History? The copied files aren't touched.")) return;
            History.Delete(r);
            _records = History.List();
            Fill();
        }
    }

    /// <summary>Favourite destination folders, also shown in File Explorer's "Copy to…" and "Move to…" menus.</summary>
    public sealed class FavouritesWindow : Window
    {
        private readonly ListBox _list = new ListBox().Styled("TableList");
        private readonly List<string> _favs = Settings.Favourites;

        public FavouritesWindow(Window owner)
        {
            Width = 620;
            Height = 480;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Favourites · KUDUCOPY", owner);

            var head = Ui.Column(Ui.Heading("Favourite folders", 18),
                Ui.Subtle("One click to copy there from the main window, and listed under \"Copy to…\" and \"Move to…\" when you right-click files in File Explorer.", 12.5)
                    .Margin(0, 4, 0, 0));
            var card = Ui.Card(_list, 0);
            card.ClipToBounds = true;

            var add = Ui.Button("Add folder…", "IconPlus", "PrimaryButton", (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose a favourite folder");
                if (folder == null || _favs.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
                _favs.Add(folder);
                Save();
            });
            var close = Ui.Button("Close");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            var buttons = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            buttons.Children.Add(add.At(0, 0));
            buttons.Children.Add(close.At(0, 2));

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(head.Margin(0, 0, 0, 14).At(0));
            root.Children.Add(card.At(1));
            root.Children.Add(buttons.Margin(0, 14, 0, 0).At(2));
            Content = root;
            Fill();
        }

        private void Fill()
        {
            _list.Items.Clear();
            for (var i = 0; i < _favs.Count; i++)
            {
                var index = i;
                var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
                g.Children.Add(Ui.Icon("IconStar", 16).Margin(14, 0, 10, 0).At(0, 0));
                g.Children.Add(Ui.Mono(_favs[i], 12.5).At(0, 1));
                g.Children.Add(Small("IconUp", "Move up", () => MoveItem(index, -1), index > 0).At(0, 2));
                g.Children.Add(Small("IconDown", "Move down", () => MoveItem(index, 1), index < _favs.Count - 1).At(0, 3));
                g.Children.Add(Small("IconTrash", "Remove", () => { _favs.RemoveAt(index); Save(); }, true).Margin(0, 0, 8, 0).At(0, 4));
                _list.Items.Add(new ListBoxItem { Content = g, MinHeight = 40 });
            }
            if (_favs.Count == 0)
                _list.Items.Add(new ListBoxItem { Content = Ui.Subtle("No favourites yet. Add the folders you copy to most often.", 12.5).Margin(14, 12, 14, 12), IsEnabled = false });
        }

        private static Button Small(string icon, string tip, Action action, bool enabled)
        {
            var b = Ui.Button(string.Empty, icon, "GhostButton", (s, e) => action()).Tip(tip);
            b.MinHeight = 30;
            b.Padding = new Thickness(8, 0, 8, 0);
            b.IsEnabled = enabled;
            return b;
        }

        private void MoveItem(int index, int by)
        {
            var target = index + by;
            if (target < 0 || target >= _favs.Count) return;
            var item = _favs[index];
            _favs.RemoveAt(index);
            _favs.Insert(target, item);
            Save();
        }

        private void Save()
        {
            Settings.Favourites = _favs;
            ShellIntegration.Refresh();
            Fill();
        }
    }

    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window owner, Func<string> robocopyVersion)
        {
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUCOPY", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("File Explorer", 14).Margin(0, 12, 0, 4));
            var menu = new CheckBox { Content = "Add \"Paste with KUDUCOPY\", \"Copy to…\" and \"Move to…\" to the right-click menu", IsChecked = Settings.ShellMenu };
            menu.Click += (s, e) =>
            {
                Settings.ShellMenu = menu.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(menu);
            root.Children.Add(Ui.Subtle("For your Windows account only, no administrator rights needed. On Windows 11 they're under \"Show more options\".", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("Copying", 14).Margin(0, 16, 0, 4));
            var remembered = Settings.Conflict;
            var conflictText = Ui.Text(remembered == Conflict.Ask
                ? "When files already exist, KUDUCOPY asks you."
                : "When files already exist: " + ConflictName(remembered) + " (remembered).", 12.5, wrap: true);
            var reset = Ui.Link("Ask me again", (s, e) =>
            {
                Settings.Conflict = Conflict.Ask;
                conflictText.Text = "When files already exist, KUDUCOPY asks you.";
            });
            root.Children.Add(Ui.Row(conflictText, reset.Margin(10, 0, 0, 0)));
            root.Children.Add(Ui.Subtle("Copy history and logs are saved on this computer only, in " + AppPaths.Root, 12).Margin(0, 6, 0, 0));
            root.Children.Add(Ui.Link("Open the History folder", (s, e) => AppInfo.OpenFolder(AppPaths.History)).Margin(0, 2, 0, 0));

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUCOPY version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUCOPY", (s, e) => About.Show(this, robocopyVersion())).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }

        private static string ConflictName(Conflict c)
        {
            switch (c)
            {
                case Conflict.Overwrite: return "overwrite";
                case Conflict.ReplaceIfNewer: return "replace if newer";
                case Conflict.Skip: return "skip";
                case Conflict.KeepBoth: return "keep both";
                default: return "ask";
            }
        }
    }

    public static class About
    {
        public static void Show(Window owner, string robocopyVersion)
        {
            var tile = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(15),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xB3, 0x6B)),
                Child = new LogoMark(46) { Margin = new Thickness(9) }
            };
            ((LogoMark)tile.Child).SetValue(TextElement.ForegroundProperty, System.Windows.Media.Brushes.White);

            var header = Ui.Row(tile, Ui.Column(Ui.Text("KUDUCOPY", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Fast, reliable file copying", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(120), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            Row("Made by", Ui.Text(AppInfo.Author, 12.5));
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Copy engine", Ui.Text("Robocopy " + (string.IsNullOrEmpty(robocopyVersion) ? string.Empty : robocopyVersion + " ") + "(built into Windows)", 12.5));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUCOPY runs Robocopy, which is part of Windows, and doesn't include or change it. " +
                          "KUDUCOPY is not affiliated with, sponsored or endorsed by Microsoft. Windows and Robocopy are names of Microsoft.", 11.5).Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUCOPY", content, 560, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUCOPY {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            window.ShowDialog();
        }

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }
}
