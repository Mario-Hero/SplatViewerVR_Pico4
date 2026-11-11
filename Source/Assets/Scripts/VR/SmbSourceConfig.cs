// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// One SMB/CIFS share as declared in smb_shares.json. Plain data only, so it is
/// safe to hand to worker threads after Reload().
/// </summary>
[Serializable]
public sealed class SmbSource
{
    public bool enabled = false;
    public string id = "";
    public string name = "";
    public string host = "";
    public string share = "";
    public string path = "";
    public string domain = "";
    public string username = "";
    public string password = "";

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (!string.IsNullOrWhiteSpace(host)) return host;
            return id;
        }
    }

    public bool IsValid
    {
        get { return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share); }
    }
}

[Serializable]
sealed class SmbSourceDocument
{
    public List<SmbSource> shares = new List<SmbSource>();
}

/// <summary>
/// Reads SMB share definitions from persistentDataPath/Splats/smb_shares.json.
/// Unity APIs (JsonUtility, Debug) are only used on the main thread; worker
/// threads consume the immutable snapshot returned by GetEnabledSources().
/// </summary>
public static class SmbSourceConfig
{
    public const string ConfigFileName = "smb_shares.json";

    const string Template = @"{
  ""shares"": [
    {
      ""enabled"": false,
      ""id"": ""nas"",
      ""name"": ""My NAS"",
      ""host"": ""192.168.1.10"",
      ""share"": ""Splats"",
      ""path"": """",
      ""domain"": """",
      ""username"": """",
      ""password"": """"
    }
  ]
}
";

    static readonly object Gate = new object();
    static List<SmbSource> s_sources = new List<SmbSource>();
    static string s_configPath;
    static string s_activeConfigPath;
    static string s_error;

    public static string ConfigPath
    {
        get
        {
            lock (Gate)
            {
                if (s_configPath == null)
                    s_configPath = Path.Combine(QuestSplatStorage.GetRootFolder(), ConfigFileName);
                return s_configPath;
            }
        }
    }

    /// <summary>
    /// Optional second location in the public Downloads folder. It is easier to
    /// edit over USB than the app-private Splats folder, so it wins when present.
    /// </summary>
    public static string DownloadConfigPath
    {
        get
        {
            string downloads = QuestSplatStorage.GetDownloadFolder();
            return string.IsNullOrEmpty(downloads) ? null : Path.Combine(downloads, ConfigFileName);
        }
    }

    public static string LastError
    {
        get { lock (Gate) return s_error; }
    }

    /// <summary>The file that was actually read by the last Reload().</summary>
    public static string ActiveConfigPath
    {
        get { lock (Gate) return s_activeConfigPath ?? s_configPath; }
    }

    static string ResolveConfigFile()
    {
        string download = DownloadConfigPath;
        if (!string.IsNullOrEmpty(download))
        {
            try { if (File.Exists(download)) return download; }
            catch (Exception) { }
        }
        return ConfigPath;
    }

    public static int EnabledCount
    {
        get
        {
            lock (Gate)
            {
                int count = 0;
                foreach (var source in s_sources)
                    if (source != null && source.enabled && source.IsValid) count++;
                return count;
            }
        }
    }

    public static List<SmbSource> GetEnabledSources()
    {
        lock (Gate)
        {
            var result = new List<SmbSource>();
            foreach (var source in s_sources)
                if (source != null && source.enabled && source.IsValid)
                    result.Add(source);
            return result;
        }
    }

    public static SmbSource FindById(string id)
    {
        lock (Gate)
        {
            foreach (var source in s_sources)
                if (source != null && string.Equals(source.id, id, StringComparison.OrdinalIgnoreCase))
                    return source;
            return null;
        }
    }

    /// <summary>Creates the sample config on first launch, then loads it. Main thread only.</summary>
    public static void Initialize()
    {
        try
        {
            string file = ConfigPath;
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(file)) File.WriteAllText(file, Template);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SMB] Cannot create sample connection file: " + ex.Message);
        }
        Reload();
    }

    /// <summary>Re-reads the config. Main thread only.</summary>
    public static void Reload()
    {
        string file = ResolveConfigFile();
        lock (Gate) s_activeConfigPath = file;

        try
        {
            if (!File.Exists(file))
            {
                lock (Gate) { s_sources = new List<SmbSource>(); s_error = "Connection file not found."; }
                return;
            }

            var document = JsonUtility.FromJson<SmbSourceDocument>(File.ReadAllText(file));
            var raw = (document != null && document.shares != null)
                ? document.shares
                : new List<SmbSource>();

            var normalized = new List<SmbSource>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < raw.Count; i++)
            {
                var source = raw[i];
                if (source == null) continue;

                string safe = SplatFileSystem.SanitizeSegment(source.id);
                if (string.IsNullOrEmpty(safe)) safe = "share" + (i + 1);
                if (!usedIds.Add(safe)) safe = safe + "_" + (i + 1);
                source.id = safe;
                normalized.Add(source);
            }

            lock (Gate) { s_sources = normalized; s_error = null; }
            Debug.Log("[SMB] Loaded " + normalized.Count + " share definition(s) from " + file);
        }
        catch (Exception ex)
        {
            lock (Gate) { s_sources = new List<SmbSource>(); s_error = ex.Message; }
            Debug.LogWarning("[SMB] Cannot read " + file + ": " + ex.Message);
        }
    }
}
