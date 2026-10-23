using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kuducopy.Controls;
using Kuducopy.Core;

namespace Kuducopy.Views
{
    /// <summary>"N files already exist": one question for the whole job, or a choice per file.</summary>
    public static class ConflictWindows
    {
        private static string Describe(long size, DateTime utc) =>
            Format.Size(size) + " · " + (utc == DateTime.MinValue ? "?" : utc.ToLocalTime().ToString("d MMM yyyy"));

        private static (string Text, string Brush) Comparison(PlannedFile f)
        {
            switch (f.Existing)
            {
                case Existing.Same: return ("Same", "SubtleText");
                case Existing.SourceNewer: return ("Yours is newer", "OkText");
                case Existing.SourceOlder: return ("Theirs is newer", "WarnText");
                default: return ("Different size", "WarnText");
            }
        }

        public static ConflictAnswer Ask(Window owner, ConflictQuestion q)
        {
            if (SelfTest.Running) return SelfTest.ConflictAnswer ?? new ConflictAnswer { Policy = Conflict.Skip };

            var n = q.Conflicts.Count;
            var head = Ui.Row(Ui.Icon("IconWarn", 18).Res(TextElement.ForegroundProperty, "WarnText"),
                Ui.Text($"{Format.Count(n, "file")} already exist{(n == 1 ? "s" : "")} in {q.Destination}", 17, bold: true).Margin(10, 0, 0, 0));
            ((TextBlock)head.Children[1]).TextWrapping = TextWrapping.Wrap;
            ((TextBlock)head.Children[1]).MaxWidth = 560;

            var table = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var w in new[] { Ui.Star(1.2), Ui.Star(), Ui.Star(), Ui.Auto }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var titles = new[] { "File", "You're copying", "Already there", "" };
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < titles.Length; i++) table.Children.Add(Ui.Text(titles[i], 12, "SubtleText", bold: true).Margin(0, 0, 8, 6).At(0, i));
            var row = 1;
            foreach (var f in q.Conflicts.Take(3))
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var (text, brush) = Comparison(f);
                table.Children.Add(Ui.Text(Path.GetFileName(f.Source), 12).Margin(0, 4, 8, 4).Tip(f.Source).At(row, 0));
                table.Children.Add(Ui.Subtle(Describe(f.Size, f.SourceTime), 12, false).Margin(0, 4, 8, 4).At(row, 1));
                table.Children.Add(Ui.Subtle(Describe(f.DestSize, f.DestTime), 12, false).Margin(0, 4, 8, 4).At(row, 2));
                table.Children.Add(Ui.Text(text, 12, brush).Margin(0, 4, 0, 4).At(row, 3));
                row++;
            }
            if (n > 3)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                table.Children.Add(Ui.Subtle($"and {n - 3:N0} more…", 12, false).At(row, 0, 4));
            }

            var choices = new List<(Conflict Policy, string Title, string Text)>
            {
                (Conflict.Overwrite, "Overwrite all", $"Replace the {n:N0} files in {q.Destination} with these ones."),
                (Conflict.ReplaceIfNewer, "Replace if newer", $"Only replace files where the one you're copying is newer. {q.NewerCount:N0} of {n:N0}."),
                (Conflict.Skip, "Skip all", "Keep the files already there. Copy everything else."),
                (Conflict.KeepBoth, "Keep both", "Copy these as new files, e.g. \"photo (2).jpg\"."),
                (Conflict.Decide, "Let me decide for each file", $"See the {n:N0} files side by side and choose one by one.")
            };
            var options = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var selected = q.NewerCount > 0 ? Conflict.ReplaceIfNewer : Conflict.Skip;
            foreach (var c in choices)
            {
                var content = Ui.Column(Ui.Text(c.Title, 13, bold: true), Ui.Subtle(c.Text, 12));
                var rb = new RadioButton { Content = content, GroupName = "conflict", IsChecked = c.Policy == selected }.Styled("ChipRadio");
                rb.HorizontalAlignment = HorizontalAlignment.Stretch;
                rb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                rb.Margin = new Thickness(0, 6, 0, 0);
                var policy = c.Policy;
                rb.Checked += (s, e) => selected = policy;
                options.Children.Add(rb);
            }

            var remember = new CheckBox { Content = "Remember this choice for future copies (change it in the main window)" }.Margin(0, 12, 0, 0);
            var content2 = Ui.Column(head, Ui.Subtle("Found before copying started. Nothing has been changed yet.", 12.5).Margin(28, 2, 0, 0), table, options, remember);

            var ok = Ui.Button("Continue", null, "PrimaryButton");
            var cancel = Ui.Button("Cancel job");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            var window = Dialogs.Dialog(owner, "Files already exist · KUDUCOPY", content2, 660, cancel, ok);
            var answer = new ConflictAnswer { Cancel = true };
            ok.Click += (s, e) =>
            {
                answer = new ConflictAnswer { Policy = selected, Remember = remember.IsChecked == true && selected != Conflict.Decide };
                window.Close();
            };
            window.ShowDialog();
            return answer;
        }

        /// <summary>Choose per file. Returns null if cancelled.</summary>
        public static Dictionary<PlannedFile, Conflict> Decide(Window owner, List<PlannedFile> conflicts)
        {
            if (SelfTest.Running) return conflicts.ToDictionary(c => c, c => SelfTest.DecideEach);

            var choice = conflicts.ToDictionary(c => c, c => c.Existing == Existing.SourceNewer ? Conflict.Overwrite : Conflict.Skip);
            var list = new StackPanel();
            var groups = new List<RadioButton[]>();
            const int max = 2000;

            foreach (var f in conflicts.Take(max))
            {
                var (text, brush) = Comparison(f);
                var g = Ui.Columns(Ui.Star(1.3), Ui.Star(), Ui.Star(), Ui.Auto);
                g.Children.Add(Ui.Column(Ui.Text(Path.GetFileName(f.Source), 12.5, bold: true), Ui.Subtle(Path.GetDirectoryName(f.Dest), 11.5, false)).Tip(f.Dest).At(0, 0));
                g.Children.Add(Ui.Column(Ui.Subtle("Yours: " + Describe(f.Size, f.SourceTime), 12, false), Ui.Text(text, 11.5, brush)).At(0, 1));
                g.Children.Add(Ui.Subtle("There: " + Describe(f.DestSize, f.DestTime), 12, false).At(0, 2));
                var buttons = new[] { Choice("Replace", Conflict.Overwrite), Choice("Skip", Conflict.Skip), Choice("Keep both", Conflict.KeepBoth) };
                var group = "d" + groups.Count;
                var file = f;
                foreach (var b in buttons)
                {
                    b.GroupName = group;
                    b.IsChecked = (Conflict)b.Tag == choice[f];
                    b.Checked += (s, e) => choice[file] = (Conflict)((RadioButton)s).Tag;
                }
                groups.Add(buttons);
                g.Children.Add(MainWindow.Segments(buttons).At(0, 3));
                list.Children.Add(new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8) }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }

            void All(Conflict c)
            {
                foreach (var key in choice.Keys.ToList()) choice[key] = c;
                foreach (var buttons in groups) foreach (var b in buttons) b.IsChecked = (Conflict)b.Tag == c;
            }

            var bulk = Ui.Row(Ui.Subtle("For all files:", 12.5, false), Ui.Link("Replace", (s, e) => All(Conflict.Overwrite)).Margin(10, 0, 0, 0),
                Ui.Link("Skip", (s, e) => All(Conflict.Skip)).Margin(10, 0, 0, 0), Ui.Link("Keep both", (s, e) => All(Conflict.KeepBoth)).Margin(10, 0, 0, 0),
                Ui.Link("Replace only newer", (s, e) =>
                {
                    foreach (var key in choice.Keys.ToList()) choice[key] = key.Existing == Existing.SourceNewer ? Conflict.Overwrite : Conflict.Skip;
                    for (var i = 0; i < groups.Count; i++)
                        foreach (var b in groups[i]) b.IsChecked = (Conflict)b.Tag == choice[conflicts[i]];
                }).Margin(10, 0, 0, 0));

            var note = conflicts.Count > max ? Ui.Subtle($"Showing the first {max:N0} of {conflicts.Count:N0}. \"For all files\" applies to every file.", 12) : null;
            var content = Ui.Column(Ui.Heading($"Choose for each of the {Format.Count(conflicts.Count, "file")}", 17), bulk.Margin(0, 10, 0, 6));
            if (note != null) content.Children.Add(note);
            content.Children.Add(new ScrollViewer { Content = list, Height = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var ok = Ui.Button("Continue", null, "PrimaryButton");
            var cancel = Ui.Button("Cancel job");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            var window = Dialogs.Dialog(owner, "Choose for each file · KUDUCOPY", content, 900, cancel, ok);
            Dictionary<PlannedFile, Conflict> result = null;
            ok.Click += (s, e) =>
            {
                result = choice;
                window.Close();
            };
            window.ShowDialog();
            return result;
        }

        private static RadioButton Choice(string text, Conflict c) =>
            new RadioButton { Content = text, Tag = c, FontSize = 12, MinHeight = 28, Padding = new Thickness(10, 0, 10, 0) }.Styled("SegmentRadio");
    }
}
