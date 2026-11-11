// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>One row in the viewer's file browser, regardless of where it lives.</summary>
public sealed class SplatEntry
{
    public string name;
    public string localPath;
    public bool isDir;
    public bool isSmbSource;
    public bool isSmbSetup;
    public bool isRemote;
    public long size;
}

/// <summary>Progress of an in-flight SMB download.</summary>
public struct SmbTransferProgress
{
    public long bytesDone;
    public long bytesTotal;
    public bool active;
}

sealed class SmbRemoteMeta
{
    public SmbSource source;
    public string remotePath;
    public long size;
    public DateTime lastWriteUtc;
}

/// <summary>
/// Unified file system facade for the viewer.
///
/// Remote SMB folders are mirrored into Application.persistentDataPath/SmbCache
/// using local paths with the same folder layout. Every downstream component
/// (RuntimeSplatLoader, SplatCycler, VRFileBrowser) keeps working with ordinary
/// local path strings; the only difference is that a remote file must be
/// materialized with EnsureLocalCached before it is decoded.
///
/// Initialize() must be called on the main thread once. All network and disk
/// work afterwards is safe from Task.Run worker threads.
/// </summary>
public static class SplatFileSystem
{
    static readonly object Gate = new object();
    static readonly Dictionary<string, SmbRemoteMeta> RemoteMeta =
        new Dictionary<string, SmbRemoteMeta>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, List<SplatEntry>> Listings =
        new Dictionary<string, List<SplatEntry>>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, object> FileLocks =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<bool>> ActiveDownloads =
        new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, SmbTransferProgress> TransferProgress =
        new Dictionary<string, SmbTransferProgress>(StringComparer.OrdinalIgnoreCase);

    static string s_localRoot;
    static string s_cacheRoot;

    /// <summary>Main thread only. Caches Unity-dependent paths for worker threads.</summary>
    public static void Initialize()
    {
        QuestSplatStorage.EnsureRootFolderExists();
        s_localRoot = QuestSplatStorage.GetRootFolder();
        s_cacheRoot = QuestSplatStorage.GetSmbCacheRoot();
        try { Directory.CreateDirectory(s_cacheRoot); }
        catch (Exception ex) { Debug.LogWarning("[SMB] Cannot create cache folder: " + ex.Message); }
        SmbSourceConfig.Initialize();
    }

    static string RequireCacheRoot()
    {
        string root = s_cacheRoot;
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException("SplatFileSystem.Initialize() must run on the main thread first.");
        return root;
    }

    public static string LocalBrowseRoot
    {
        get
        {
            if (string.IsNullOrEmpty(s_localRoot))
                throw new InvalidOperationException("SplatFileSystem.Initialize() must run on the main thread first.");
            return s_localRoot;
        }
    }

    public static string CacheRoot { get { return RequireCacheRoot(); } }

    public static string GetSourceRoot(SmbSource source)
    {
        return Path.Combine(RequireCacheRoot(), SanitizeSegment(source.id));
    }

    public static bool IsRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string root = NormalizeDir(RequireCacheRoot());
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    public static bool IsSourceRoot(string path)
    {
        if (!IsRemotePath(path)) return false;
        string relative = GetCacheRelativePath(path);
        return relative.Length > 0 &&
            relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
            relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
    }

    public static string GetCacheRelativePath(string path)
    {
        string root = NormalizeDir(RequireCacheRoot());
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return full.Substring(root.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string GetSourceIdFromPath(string path)
    {
        string relative = GetCacheRelativePath(path);
        if (relative.Length == 0) return null;
        int slash = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return slash < 0 ? relative : relative.Substring(0, slash);
    }

    /// <summary>Path relative to the configured start folder inside the share.</summary>
    public static string GetRemoteRelativePath(string path)
    {
        string relative = GetCacheRelativePath(path);
        int slash = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        string tail = slash < 0 ? string.Empty : relative.Substring(slash + 1);
        return SmbShareClient.NormalizeRemotePath(tail);
    }

    public static bool HasRemoteMetadata(string path)
    {
        lock (Gate) return RemoteMeta.ContainsKey(path);
    }

    /// <summary>
    /// True when a remote file still has to be fetched (or re-fetched because
    /// its cached copy no longer matches the size reported by the share).
    /// UI code uses this to start a worker-thread transfer instead of letting
    /// the decoder block the main thread.
    /// </summary>
    public static bool NeedsDownload(string path)
    {
        if (!IsRemotePath(path)) return false;

        SmbRemoteMeta meta;
        lock (Gate)
        {
            if (!RemoteMeta.TryGetValue(path, out meta)) return false;
        }
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || info.Length != meta.size;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Size in bytes, from SMB metadata when available, otherwise from disk.</summary>
    public static long? GetKnownFileSize(string path)
    {
        SmbRemoteMeta meta;
        lock (Gate)
        {
            if (RemoteMeta.TryGetValue(path, out meta)) return meta.size;
        }
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (long?)info.Length : null;
        }
        catch (Exception) { return null; }
    }

    public static List<string> GetCachedFiles(string folderPath)
    {
        lock (Gate)
        {
            List<SplatEntry> listing;
            if (!Listings.TryGetValue(folderPath, out listing))
                return new List<string>();

            var result = new List<string>();
            foreach (var entry in listing)
                if (!entry.isDir && entry.localPath != null)
                    result.Add(entry.localPath);
            return result;
        }
    }

    /// <summary>
    /// Lists a folder. Local folders are read synchronously from disk; remote
    /// folders use SMB when allowNetwork is true, otherwise the last listing is
    /// replayed from memory. Safe to call from a worker thread.
    /// </summary>
    public static List<SplatEntry> ListEntries(string path, bool allowNetwork)
    {
        if (string.IsNullOrWhiteSpace(path)) path = LocalBrowseRoot;

        if (IsRemotePath(path))
            return allowNetwork ? ListRemote(path) : ReplayListing(path);

        return ListLocal(path);
    }

    static List<SplatEntry> ListLocal(string path)
    {
        var result = new List<SplatEntry>();

        if (IsSameFolder(path, LocalBrowseRoot))
        {
            // Public Downloads is one level "up" from the library root, so show
            // it as an entry here to keep it reachable after the user goes back.
            if (QuestSplatStorage.DownloadFolderAvailable())
            {
                result.Add(new SplatEntry
                {
                    name = "Download (public)",
                    localPath = QuestSplatStorage.GetDownloadFolder(),
                    isDir = true
                });
            }

            foreach (var source in SmbSourceConfig.GetEnabledSources())
            {
                result.Add(new SplatEntry
                {
                    name = "[SMB] " + source.DisplayName + " (" + source.host + "/" + source.share + ")",
                    localPath = GetSourceRoot(source),
                    isDir = true,
                    isSmbSource = true
                });
            }

            // Always expose the editor entry so a share can be configured from
            // inside the headset, even when nothing is enabled yet.
            result.Add(new SplatEntry
            {
                name = SmbSourceConfig.EnabledCount > 0
                    ? "[SMB] Edit connection..."
                    : "[SMB] Set up a share...",
                localPath = string.Empty,
                isDir = false,
                isSmbSetup = true
            });
        }

        foreach (string dir in Directory.GetDirectories(path)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new SplatEntry
            {
                name = Path.GetFileName(dir),
                localPath = dir,
                isDir = true
            });
        }

        foreach (string file in Directory.GetFiles(path)
            .Where(RuntimeSplatLoader.IsSupportedFileExtension)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            result.Add(new SplatEntry
            {
                name = Path.GetFileName(file),
                localPath = file,
                isDir = false,
                size = info.Exists ? info.Length : 0L
            });
        }

        StoreListing(path, result);
        return result;
    }

    static List<SplatEntry> ListRemote(string path)
    {
        string id = GetSourceIdFromPath(path);
        SmbSource source = SmbSourceConfig.FindById(id);
        if (source == null)
            throw new IOException("No enabled SMB share matches '" + id + "'. Check smb_shares.json.");

        string remoteRelative = GetRemoteRelativePath(path);
        List<SmbRemoteEntry> remoteEntries;
        using (var client = new SmbShareClient(source))
            remoteEntries = client.ListDirectory(remoteRelative);

        var result = new List<SplatEntry>();
        foreach (var remote in remoteEntries)
        {
            if (!remote.isDir && !RuntimeSplatLoader.IsSupportedFileExtension(remote.name))
                continue;
            if (remote.name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                continue; // defensive: SMB names never contain separators

            string localPath = Path.Combine(path, SanitizeSegment(remote.name));
            var entry = new SplatEntry
            {
                name = remote.name,
                localPath = localPath,
                isDir = remote.isDir,
                isRemote = true,
                size = remote.size
            };
            result.Add(entry);

            if (remote.isDir)
            {
                try { Directory.CreateDirectory(localPath); }
                catch (Exception) { }
            }
            else
            {
                lock (Gate)
                {
                    RemoteMeta[localPath] = new SmbRemoteMeta
                    {
                        source = source,
                        remotePath = remote.remotePath,
                        size = remote.size,
                        lastWriteUtc = remote.lastWriteUtc
                    };
                }
            }
        }

        result.Sort(delegate (SplatEntry a, SplatEntry b)
        {
            if (a.isDir != b.isDir) return a.isDir ? -1 : 1;
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });

        StoreListing(path, result);
        return result;
    }

    static List<SplatEntry> ReplayListing(string path)
    {
        lock (Gate)
        {
            List<SplatEntry> listing;
            return Listings.TryGetValue(path, out listing)
                ? new List<SplatEntry>(listing)
                : new List<SplatEntry>();
        }
    }

    static void StoreListing(string path, List<SplatEntry> entries)
    {
        lock (Gate) Listings[path] = new List<SplatEntry>(entries);
    }

    /// <summary>
    /// Guarantees that path exists locally with the expected content. Safe to
    /// call repeatedly; downloads are serialized per file. Blocking — call from
    /// a worker thread (or after an async transfer already completed).
    /// </summary>
    public static void EnsureLocalCached(string path)
    {
        if (!IsRemotePath(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Splat file not found", path);
            return;
        }

        object fileLock = GetFileLock(path);
        lock (fileLock)
        {
            SmbRemoteMeta meta;
            lock (Gate) RemoteMeta.TryGetValue(path, out meta);

            if (meta == null)
            {
                if (File.Exists(path)) return; // already downloaded in an earlier session
                throw new IOException("No SMB metadata for '" + Path.GetFileName(path) +
                    "'. Re-open the network folder first.");
            }

            var info = new FileInfo(path);
            if (info.Exists && info.Length == meta.size)
                return;

            SetProgress(path, 0L, meta.size, true);
            try
            {
                using (var client = new SmbShareClient(meta.source))
                {
                    client.Download(meta.remotePath, path, meta.size,
                        delegate (long done, long total) { SetProgress(path, done, total, true); });
                }
            }
            catch (Exception)
            {
                ClearProgress(path);
                throw;
            }
            ClearProgress(path);
        }
    }

    public static Task<bool> EnsureLocalCachedAsync(string path)
    {
        lock (Gate)
        {
            Task<bool> running;
            if (ActiveDownloads.TryGetValue(path, out running) && !running.IsCompleted)
                return running;

            var task = Task.Run(delegate { EnsureLocalCached(path); return true; });
            ActiveDownloads[path] = task;
            return task;
        }
    }

    public static void ForgetDownload(string path)
    {
        lock (Gate) ActiveDownloads.Remove(path);
    }

    public static SmbTransferProgress GetTransferProgress(string path)
    {
        lock (Gate)
        {
            SmbTransferProgress progress;
            return TransferProgress.TryGetValue(path, out progress)
                ? progress
                : default(SmbTransferProgress);
        }
    }

    static void SetProgress(string path, long done, long total, bool active)
    {
        lock (Gate)
        {
            TransferProgress[path] = new SmbTransferProgress
            {
                bytesDone = done,
                bytesTotal = total,
                active = active
            };
        }
    }

    static void ClearProgress(string path)
    {
        lock (Gate) TransferProgress.Remove(path);
    }

    static object GetFileLock(string path)
    {
        lock (Gate)
        {
            object result;
            if (!FileLocks.TryGetValue(path, out result))
            {
                result = new object();
                FileLocks[path] = result;
            }
            return result;
        }
    }

    // Windows SMB shares never contain these characters in a name, but being
    // explicit keeps cache paths identical on every platform (Android's
    // GetInvalidFileNameChars only returns '/' and '\0').
    static readonly char[] ReservedChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' };

    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (Array.IndexOf(invalid, c) >= 0 || Array.IndexOf(ReservedChars, c) >= 0 || c < ' ')
                chars[i] = '_';
        }
        string result = new string(chars).Trim('.', ' ');
        if (result.Length > 60) result = result.Substring(0, 60);
        return result;
    }

    static string NormalizeDir(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    static bool IsSameFolder(string lhs, string rhs)
    {
        if (string.IsNullOrWhiteSpace(lhs) || string.IsNullOrWhiteSpace(rhs)) return false;
        try { return string.Equals(NormalizeDir(lhs), NormalizeDir(rhs), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
