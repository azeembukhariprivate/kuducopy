using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Kuducopy.Core
{
    /// <summary>Where KUDUCOPY keeps its files: %LOCALAPPDATA%\KUDUCOPY, for the current Windows user only.</summary>
    public static class AppPaths
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                    _root = Path.Combine(local, "KUDUCOPY");
                }
                return _root;
            }
            set => _root = value;
        }

        public static string History => Ensure(Path.Combine(Root, "History"));
        public static string Temp => Ensure(Path.Combine(Root, "Temp"));

        private static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static class Json
    {
        public static void Write<T>(string path, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            var temp = path + ".tmp";
            using (var stream = File.Create(temp)) serializer.WriteObject(stream, value);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static T Read<T>(string path) where T : class
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = File.OpenRead(path)) return serializer.ReadObject(stream) as T;
            }
            catch
            {
                return null;
            }
        }
    }

    public enum JobResult { Done, DoneWithProblems, Cancelled, Failed }

    [DataContract]
    public sealed class FailedFile
    {
        [DataMember] public string Source { get; set; }
        [DataMember] public string Dest { get; set; }
        [DataMember] public string Reason { get; set; }
    }

    /// <summary>One finished job, as shown in History.</summary>
    [DataContract]
    public sealed class JobRecord
    {
        [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public DateTime Started { get; set; }
        [DataMember] public DateTime Finished { get; set; }
        [DataMember] public CopyMode Mode { get; set; }
        [DataMember] public string From { get; set; } = string.Empty;
        [DataMember] public string To { get; set; } = string.Empty;
        [DataMember] public int Files { get; set; }
        [DataMember] public long Bytes { get; set; }
        [DataMember] public int Copied { get; set; }
        [DataMember] public int Skipped { get; set; }
        [DataMember] public bool Verified { get; set; }
        [DataMember] public JobResult Result { get; set; }
        [DataMember] public string Message { get; set; } = string.Empty;
        [DataMember] public List<FailedFile> Failed { get; set; } = new List<FailedFile>();
        [DataMember] public JobSpec Spec { get; set; }

        public string LogPath => Path.Combine(AppPaths.History, Id + ".log");
        public string JsonPath => Path.Combine(AppPaths.History, Id + ".json");

        public string WhenText
        {
            get
            {
                var today = DateTime.Today;
                if (Started.Date == today) return "Today, " + Started.ToString("HH:mm");
                if (Started.Date == today.AddDays(-1)) return "Yesterday, " + Started.ToString("HH:mm");
                return Started.ToString(Started.Year == today.Year ? "d MMM, HH:mm" : "d MMM yyyy, HH:mm");
            }
        }

        public string ResultText
        {
            get
            {
                switch (Result)
                {
                    case JobResult.Done: return Verified ? "Done · verified" : "Done";
                    case JobResult.DoneWithProblems: return Failed.Count == 1 ? "1 failed" : Failed.Count + " failed";
                    case JobResult.Cancelled: return "Cancelled";
                    default: return "Failed";
                }
            }
        }
    }

    public static class History
    {
        public static void Save(JobRecord record) => Json.Write(record.JsonPath, record);

        public static List<JobRecord> List() =>
            Directory.GetFiles(AppPaths.History, "*.json")
                .Select(f => Json.Read<JobRecord>(f))
                .Where(r => r != null)
                .OrderByDescending(r => r.Started)
                .ToList();

        public static void Delete(JobRecord record)
        {
            foreach (var path in new[] { record.JsonPath, record.LogPath })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* ignore */ }
            }
        }

        /// <summary>Keeps the newest jobs only.</summary>
        public static void Trim(int keep = 200)
        {
            foreach (var old in List().Skip(keep)) Delete(old);
        }
    }

    /// <summary>A job built to copy failed files again, to the same place.</summary>
    public static class Retry
    {
        public static JobSpec For(JobSpec original, IEnumerable<FailedFile> failed)
        {
            var spec = original.Clone();
            spec.Sources = new List<string>();
            spec.Conflict = Conflict.Overwrite; // replace any half-copied files
            spec.Tasks = failed
                .Where(f => !string.IsNullOrEmpty(f.Source) && !string.IsNullOrEmpty(f.Dest))
                // "Keep both" copies were renamed by KUDUCOPY; never retry them over the original file.
                .Where(f => string.Equals(Path.GetFileName(f.Source), Path.GetFileName(f.Dest), StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => (Path.GetDirectoryName(f.Source) ?? string.Empty) + "|" + (Path.GetDirectoryName(f.Dest) ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(g => new RoboTask
                {
                    SourceDir = Path.GetDirectoryName(g.First().Source),
                    DestDir = Path.GetDirectoryName(g.First().Dest),
                    Files = g.Select(f => Path.GetFileName(f.Source)).ToList()
                })
                .ToList();
            return spec;
        }
    }
}
