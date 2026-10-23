using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Kuducopy.Core
{
    /// <summary>
    /// The File Explorer right-click menu: "Paste with KUDUCOPY", "Copy to…" and "Move to…" with favourite folders.
    /// Stored under HKEY_CURRENT_USER\Software\Classes, so it needs no administrator rights and affects this user only.
    /// </summary>
    public static class ShellIntegration
    {
        private const string Classes = @"Software\Classes\";
        private const string PasteVerb = "KUDUCOPY.Paste";
        private const string CopyVerb = "KUDUCOPY.CopyTo";
        private const string MoveVerb = "KUDUCOPY.MoveTo";

        /// <summary>The registry entries to create: key path → (value name → value). Exposed for tests.</summary>
        public static Dictionary<string, Dictionary<string, string>> Entries(string exe, IList<string> favourites)
        {
            var e = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var quoted = "\"" + exe + "\"";
            var icon = exe + ",0";

            void Verb(string key, string label, string command, bool multi = false)
            {
                var values = new Dictionary<string, string> { ["MUIVerb"] = label, ["Icon"] = icon };
                if (multi) values["MultiSelectModel"] = "Player";
                e[key] = values;
                e[key + @"\command"] = new Dictionary<string, string> { [""] = command };
            }

            // Paste into the folder that's open (background) or the folder that was right-clicked.
            Verb(@"Directory\Background\shell\" + PasteVerb, "Paste with KUDUCOPY", quoted + " --paste \"%V\"");
            Verb(@"Directory\shell\" + PasteVerb, "Paste into this folder with KUDUCOPY", quoted + " --paste \"%1\"");
            Verb(@"Drive\shell\" + PasteVerb, "Paste with KUDUCOPY", quoted + " --paste \"%1\"");

            // Copy to… / Move to… submenus on files and folders.
            foreach (var target in new[] { "*", "Directory" })
            {
                foreach (var (verb, label, flag) in new[] { (CopyVerb, "Copy to…", "--copy-to"), (MoveVerb, "Move to…", "--move-to") })
                {
                    var key = target + @"\shell\" + verb;
                    e[key] = new Dictionary<string, string> { ["MUIVerb"] = label, ["Icon"] = icon, ["SubCommands"] = "", ["MultiSelectModel"] = "Player" };
                    for (var i = 0; i < favourites.Count; i++)
                    {
                        var fav = favourites[i];
                        Verb(key + $@"\shell\{i:D2}", fav, quoted + $" {flag} \"{fav}\" \"%1\"", true);
                    }
                    Verb(key + @"\shell\99", "Choose a folder…", quoted + $" {flag}-ask \"%1\"", true);
                }
            }
            return e;
        }

        public static void Register(IList<string> favourites)
        {
            RemoveAll();
            foreach (var pair in Entries(AppInfo.ExePath, favourites ?? new List<string>()))
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Classes + pair.Key))
                {
                    if (key == null) continue;
                    foreach (var v in pair.Value) key.SetValue(v.Key, v.Value);
                }
            }
        }

        public static void RemoveAll()
        {
            foreach (var path in new[]
                     {
                         @"Directory\Background\shell\" + PasteVerb, @"Directory\shell\" + PasteVerb, @"Drive\shell\" + PasteVerb,
                         @"*\shell\" + CopyVerb, @"*\shell\" + MoveVerb, @"Directory\shell\" + CopyVerb, @"Directory\shell\" + MoveVerb
                     })
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(Classes + path, false); }
                catch { /* not there */ }
            }
        }

        /// <summary>Keeps the menu in line with the setting and the favourites (e.g. after the app moved).</summary>
        public static void Refresh()
        {
            try
            {
                if (Settings.ShellMenu) Register(Settings.Favourites);
                else RemoveAll();
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
