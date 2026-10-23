using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Kuducopy.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\KUDUCOPY";

        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        public static bool ShellMenu
        {
            get => GetBool("ShellMenu", true);
            set => SetBool("ShellMenu", value);
        }

        public static bool Advanced
        {
            get => GetBool("Advanced", false);
            set => SetBool("Advanced", value);
        }

        /// <summary>"If a file already exists" choice. Ask unless the user ticked "Remember this choice".</summary>
        public static Conflict Conflict
        {
            get => GetEnum("Conflict", Core.Conflict.Ask);
            set => Set("Conflict", value.ToString());
        }

        public static bool Verify
        {
            get => GetBool("Verify", false);
            set => SetBool("Verify", value);
        }

        public static HashKind Hash
        {
            get => GetEnum("Hash", HashKind.Md5);
            set => Set("Hash", value.ToString());
        }

        public static bool KeepGoing
        {
            get => GetBool("KeepGoing", true);
            set => SetBool("KeepGoing", value);
        }

        public static string LastDestination
        {
            get => Get("LastDestination", string.Empty);
            set => Set("LastDestination", value);
        }

        public static bool ShowFileList
        {
            get => GetBool("ShowFileList", true);
            set => SetBool("ShowFileList", value);
        }

        /// <summary>Favourite destination folders, in the order the user added them.</summary>
        public static List<string> Favourites
        {
            get => Get("Favourites", string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            set => Set("Favourites", string.Join("|", (value ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(20)));
        }

        /// <summary>Advanced mode options, saved as "/E|/COPY:DAT|/XF\t*.tmp" (switch, tab, value).</summary>
        public static List<SwitchValue> Switches
        {
            get
            {
                var raw = Get("Switches", null);
                if (raw == null) return RoboOptions.Defaults();
                return raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split(new[] { '\t' }, 2))
                    .Select(p => new SwitchValue(p[0], p.Length > 1 ? p[1] : string.Empty))
                    .ToList();
            }
            set => Set("Switches", string.Join("|", (value ?? new List<SwitchValue>()).Select(s => s.Switch + "\t" + (s.Value ?? string.Empty).Replace("|", " "))));
        }

        public static string ExtraOptions
        {
            get => Get("ExtraOptions", string.Empty);
            set => Set("ExtraOptions", value);
        }
    }
}
