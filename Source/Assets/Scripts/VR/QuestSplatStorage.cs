// SPDX-License-Identifier: MIT
using System.IO;
using UnityEngine;

static class QuestSplatStorage
{
    const string SplatFolderName = "Splats";
    public const string SmbCacheFolderName = "SmbCache";

    static string s_downloadFolder;

    public static string GetBrowseRootFolder()
    {
        return GetRootFolder();
    }

    public static string GetRootFolder()
    {
        return Path.Combine(GetBaseFolder(), SplatFolderName);
    }

    /// <summary>Local mirror folder for files fetched from SMB shares.</summary>
    public static string GetSmbCacheRoot()
    {
        return Path.Combine(GetBaseFolder(), SmbCacheFolderName);
    }

    /// <summary>
    /// Public Downloads folder. On Android that is the shared /sdcard/Download
    /// where files arrive from USB, a browser download or the Pico file manager.
    /// </summary>
    public static string GetDownloadFolder()
    {
        if (!string.IsNullOrEmpty(s_downloadFolder))
            return s_downloadFolder;

        string result = null;
        if (Application.isMobilePlatform)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var env = new AndroidJavaClass("android.os.Environment"))
                using (var dir = env.CallStatic<AndroidJavaObject>(
                    "getExternalStoragePublicDirectory", "Download"))
                {
                    if (dir != null)
                        result = dir.Call<string>("getAbsolutePath");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Storage] Cannot resolve public Downloads folder: " + ex.Message);
            }
#endif
            if (string.IsNullOrEmpty(result))
                result = "/storage/emulated/0/Download";
        }
        else
        {
            string home = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile);
            result = string.IsNullOrEmpty(home)
                ? null
                : Path.Combine(home, "Downloads");
        }

        s_downloadFolder = result;
        return result;
    }

    public static bool IsDownloadFolder(string path)
    {
        string root = GetDownloadFolder();
        if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(path))
            return false;
        try { return string.Equals(Normalize(path), Normalize(root), System.StringComparison.OrdinalIgnoreCase); }
        catch (System.Exception) { return false; }
    }

    /// <summary>True when public Downloads exists and can be listed at all.</summary>
    public static bool DownloadFolderAvailable()
    {
        string folder = GetDownloadFolder();
        if (string.IsNullOrEmpty(folder)) return false;
        try { return Directory.Exists(folder); }
        catch (System.Exception) { return false; }
    }

    /// <summary>The folder the browser opens on start: Downloads on Android.</summary>
    public static string GetDefaultBrowseFolder()
    {
        if (Application.isMobilePlatform && DownloadFolderAvailable())
            return GetDownloadFolder();
        return GetRootFolder();
    }

    public static void EnsureRootFolderExists()
    {
        Directory.CreateDirectory(GetRootFolder());
    }

    public static void EnsureSmbCacheExists()
    {
        Directory.CreateDirectory(GetSmbCacheRoot());
    }

    /// <summary>
    /// True for paths inside the local Splats folder, the SMB cache mirror or the
    /// public Downloads folder. SMB entries are represented by local cache paths,
    /// so all three roots are valid browse locations.
    /// </summary>
    public static bool IsInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string candidate = Normalize(path);
            if (candidate.StartsWith(Normalize(GetRootFolder()), System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (candidate.StartsWith(Normalize(GetSmbCacheRoot()), System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (Application.isMobilePlatform
                && candidate.StartsWith(Normalize(GetDownloadFolder()), System.StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    public static string GetBaseFolder()
    {
        return Application.isMobilePlatform
            ? Application.persistentDataPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    static string Normalize(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }
}
