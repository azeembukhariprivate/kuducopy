using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Kuducopy.Views
{
    /// <summary>Reads the files copied (Ctrl+C) or cut (Ctrl+X) in File Explorer.</summary>
    public static class ClipboardFiles
    {
        public static List<string> Get(out bool cut)
        {
            cut = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsFileDropList()) return new List<string>();
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    // "Preferred DropEffect": 2 means the files were cut (move), 5 or 1 means copied.
                    if (Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4)
                    {
                        var bytes = new byte[4];
                        effect.Position = 0;
                        effect.Read(bytes, 0, 4);
                        cut = (BitConverter.ToInt32(bytes, 0) & 2) == 2;
                    }
                    return files;
                }
                catch
                {
                    System.Threading.Thread.Sleep(100); // the clipboard is briefly locked by another program
                }
            }
            return new List<string>();
        }
    }
}
