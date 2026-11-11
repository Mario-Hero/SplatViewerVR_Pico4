// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

[DefaultExecutionOrder(-1000)]
public sealed class ViewerStartup : MonoBehaviour
{
    // When true, the app opens the file browser at startup instead of
    // auto-loading the first splat it finds. Useful for diagnosing a black
    // screen: if the browser UI shows but a chosen splat is still black, the
    // problem is in the Gaussian splat render path, not the load pipeline.
    const bool OpenBrowserOnStart = true;

    const float StartupDelaySeconds = 0.5f;
    // Headset-native resolution. The URP asset no longer applies an extra 0.8
    // factor, so 1.0 here really means "one rendered pixel per panel pixel".
    const float QuestRenderScale = 1.00f;
    // Fixed foveated rendering: peripheral pixels are shaded at a lower rate,
    // which is a large win for a fill-rate bound splat renderer and does not
    // remove a single splat. 0.5 ≈ "medium"; the Pico plugin reads its own copy
    // of this value from PICOProjectSetting at session creation.
    const float QuestFoveatedRenderingLevel = 0.5f;
    const int QuestTargetFrameRate = 72;
    const int MaxDisplayOverrideFrames = 12;
    const GaussianSplatRenderer.SortMode QuestSortMode = GaussianSplatRenderer.SortMode.EveryNthFrame;
    const int QuestSortNthFrame = 12;
    // These 4DGS captures ship without higher-order SH bands, so order 0 is
    // pixel-identical to order 3 and much cheaper. Files that do carry SH can
    // be raised in the options menu.
    const int QuestSHOrder = 0;
    const float QuestSortPositionThreshold = 0.02f;
    const float QuestSortAngleThreshold = 3.0f;
    const int QuestSortMaxFramesWithoutUpdate = 18;
    const float QuestAlphaClipThreshold = 6.0f / 255.0f;
    const float QuestSplatEdgeSharpness = 1.6f;
    const bool QuestUseOpaqueSplatHack = false;

    static ViewerStartup s_instance;
    static readonly List<XRDisplaySubsystem> s_displays = new();
    string _pendingFilePath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (s_instance != null)
            return;

        var go = new GameObject(nameof(ViewerStartup));
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<ViewerStartup>();
    }

    void Awake()
    {
        ApplyWindowMode();
        ApplyMobilePerformanceOverrides();
        ApplyMobileSplatRendererOverrides();
        // Sets up LocalSplats + SMB cache folders and reads smb_shares.json.
        SplatFileSystem.Initialize();
        _pendingFilePath = FindLaunchFilePath();
    }

    IEnumerator Start()
    {
        // The browser starts in the public Downloads folder, which is only
        // readable once the storage permission has been granted.
        yield return EnsureStoragePermission();

        // Give XR and scene objects a moment to initialize before deciding mode and autoloading.
        yield return new WaitForSecondsRealtime(StartupDelaySeconds);
        ApplyWindowMode();
        ApplyMobilePerformanceOverrides();
        ApplyMobileSplatRendererOverrides();
        yield return ApplyMobileDisplayOverrides();
        ApplyPerformanceLevel();

        EnsureOptionsMenu();

        bool loaded = !OpenBrowserOnStart
            && !string.IsNullOrEmpty(_pendingFilePath)
            && TryAutoLoadLaunchFile(_pendingFilePath);

        InitializeDesktopCursorState();

        // An empty library renders a black screen with no visible affordance.
        // Show the world-space browser so the user can see where to put files
        // or enable an SMB share.
        if (!loaded)
            ShowEmptyLibraryHint();
    }

    /// <summary>
    /// Android 10 keeps plain File IO working on /sdcard when the app opts out of
    /// scoped storage, but the storage permission still has to be granted at
    /// runtime before the public Downloads folder can be listed.
    /// </summary>
    static IEnumerator EnsureStoragePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                UnityEngine.Android.Permission.ExternalStorageRead))
        {
            yield break;
        }

        Debug.Log("[ViewerStartup] Requesting storage permission for the Downloads folder.");
        UnityEngine.Android.Permission.RequestUserPermission(
            UnityEngine.Android.Permission.ExternalStorageRead);

        float deadline = Time.realtimeSinceStartup + 15f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.ExternalStorageRead))
            {
                Debug.Log("[ViewerStartup] Storage permission granted.");
                yield break;
            }
            yield return null;
        }

        Debug.LogWarning("[ViewerStartup] Storage permission was not granted; " +
            "the Downloads folder may not be listable and the browser falls back to Splats/.");
#else
        yield break;
#endif
    }

    static void ShowEmptyLibraryHint()
    {
        Debug.LogWarning("[ViewerStartup] No splat file loaded. Opening the file browser; " +
            "copy .ply/.spz/.spx/.sog into Splats/ or enable a share in smb_shares.json.");

        var browser = FindAnyObjectByType<VRFileBrowser>();
        if (browser != null && !browser.IsOpen)
            browser.ToggleBrowser();
    }

    static void EnsureOptionsMenu()
    {
        if (FindAnyObjectByType<VROptionsMenu>() != null)
            return;
        var go = new GameObject(nameof(VROptionsMenu));
        DontDestroyOnLoad(go);
        go.AddComponent<VROptionsMenu>();
    }

    static void ApplyWindowMode()
    {
        Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
        Screen.fullScreen = true;
    }

    static void ApplyMobilePerformanceOverrides()
    {
        if (!Application.isMobilePlatform)
            return;

        Application.targetFrameRate = QuestTargetFrameRate;
        QualitySettings.vSyncCount = 0;

        float renderScale = VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeyRenderScale, QuestRenderScale);
        renderScale = Mathf.Clamp(renderScale, 0.6f, 1.4f);
        XRSettings.eyeTextureResolutionScale = renderScale;
        Debug.Log($"[ViewerStartup] eyeTextureResolutionScale={XRSettings.eyeTextureResolutionScale:0.00} " +
                  $"eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} " +
                  $"screen={Screen.width}x{Screen.height}");
    }

    static void ApplyMobileSplatRendererOverrides()
    {
        if (!Application.isMobilePlatform)
            return;

        GaussianSplatRenderer[] renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
        int savedSHOrder = (int)VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeySHOrder, QuestSHOrder);
        int savedSortNth = (int)VROptionsMenu.LoadSavedFloat(VROptionsMenu.PrefKeySortNth, QuestSortNthFrame);
        foreach (GaussianSplatRenderer renderer in renderers)
        {
            renderer.m_SortMode = QuestSortMode;
            renderer.m_SortNthFrame = savedSortNth > 0 ? savedSortNth : QuestSortNthFrame;
            renderer.m_SortPositionThreshold = QuestSortPositionThreshold;
            renderer.m_SortAngleThreshold = QuestSortAngleThreshold;
            renderer.m_SortMaxFramesWithoutUpdate = QuestSortMaxFramesWithoutUpdate;
            renderer.m_AlphaClipThreshold = QuestAlphaClipThreshold;
            renderer.m_SplatEdgeSharpness = QuestSplatEdgeSharpness;
            renderer.m_UseOpaqueRenderHack = QuestUseOpaqueSplatHack;
            renderer.m_SHOrder = Mathf.Clamp(savedSHOrder, 0, 3);
        }

        if (renderers.Length > 0)
        {
            Debug.Log($"[ViewerStartup] Applied Quest splat overrides to {renderers.Length} renderer(s): sort={QuestSortMode}, nthFrame={QuestSortNthFrame}, posThreshold={QuestSortPositionThreshold:0.000}, angleThreshold={QuestSortAngleThreshold:0.0}, alphaClip={QuestAlphaClipThreshold:0.00}, edgeSharpness={QuestSplatEdgeSharpness:0.0}, opaqueHack={QuestUseOpaqueSplatHack}");
        }
    }

    static IEnumerator ApplyMobileDisplayOverrides()
    {
        if (!Application.isMobilePlatform)
            yield break;

        for (int frame = 0; frame < MaxDisplayOverrideFrames; frame++)
        {
            yield return null;

            s_displays.Clear();
            SubsystemManager.GetSubsystems(s_displays);

            XRDisplaySubsystem display = s_displays.FirstOrDefault(subsystem => subsystem != null && subsystem.running);
            if (display == null)
                continue;

            display.foveatedRenderingLevel = Mathf.Clamp01(QuestFoveatedRenderingLevel);
            display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None;
            Debug.Log($"[ViewerStartup] XR display overrides applied. Fixed foveated rendering level: {display.foveatedRenderingLevel:0.00}, " +
                      $"eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} at scale {XRSettings.eyeTextureResolutionScale:0.00}");
            yield break;
        }

        Debug.LogWarning("[ViewerStartup] XR display subsystem was not ready for runtime foveation overrides.");
    }

    /// <summary>
    /// Asks the Pico runtime for a higher CPU/GPU performance level than the
    /// default power-balanced governor. This does not change what is rendered —
    /// it only stops the clocks from being held back while the splat renderer is
    /// GPU bound. Requires the "OpenXR Performance Settings" feature (enabled by
    /// Assets/Editor/EnablePicoOpenXR.cs); the call is a harmless no-op without it.
    /// </summary>
    static void ApplyPerformanceLevel()
    {
        if (!Application.isMobilePlatform)
            return;

        try
        {
            if (!Unity.XR.OpenXR.Features.PICOSupport.PerformanceSettingsFeature.isExtensionEnable)
            {
                Debug.LogWarning("[ViewerStartup] XR_EXT_performance_settings is not available; " +
                                 "running at the default performance level.");
                return;
            }

            var gpu = Unity.XR.OpenXR.Features.PICOSupport.PerformanceSettingsFeature
                .XrPerfSettingsSetPerformanceLevelEXT(
                    Unity.XR.OpenXR.Features.PICOSupport.XrPerfSettingsDomainEXT.XR_PERF_SETTINGS_DOMAIN_GPU_EXT,
                    Unity.XR.OpenXR.Features.PICOSupport.XrPerfSettingsLevelEXT.XR_PERF_SETTINGS_LEVEL_BOOST_EXT);

            var cpu = Unity.XR.OpenXR.Features.PICOSupport.PerformanceSettingsFeature
                .XrPerfSettingsSetPerformanceLevelEXT(
                    Unity.XR.OpenXR.Features.PICOSupport.XrPerfSettingsDomainEXT.XR_PERF_SETTINGS_DOMAIN_CPU_EXT,
                    Unity.XR.OpenXR.Features.PICOSupport.XrPerfSettingsLevelEXT.XR_PERF_SETTINGS_LEVEL_SUSTAINED_HIGH_EXT);

            Debug.Log($"[ViewerStartup] Performance level requested: GPU boost={gpu}, CPU sustainedHigh={cpu}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ViewerStartup] Performance level request failed: {ex.Message}");
        }
    }

    static void InitializeDesktopCursorState()    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    static string FindLaunchFilePath()
    {
        if (Application.isMobilePlatform)
            return FindFirstSplatInRoot();

        string[] args;
        try
        {
            args = Environment.GetCommandLineArgs();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ViewerStartup] Could not read command line: {ex.Message}");
            return null;
        }

        return args
            .Skip(1)
            .Select(arg => arg.Trim().Trim('"'))
            .FirstOrDefault(IsSupportedLaunchFile);
    }

    static string FindFirstSplatInRoot()
    {
        string root = QuestSplatStorage.GetRootFolder();
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(RuntimeSplatLoader.IsSupportedFileExtension)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    static bool IsSupportedLaunchFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && RuntimeSplatLoader.IsSupportedFileExtension(path);
    }

    static bool TryAutoLoadLaunchFile(string filePath)
    {
        var loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (loader == null)
        {
            Debug.LogWarning($"[ViewerStartup] RuntimeSplatLoader not found for launch file: {filePath}");
            return false;
        }

        if (!loader.LoadFile(filePath))
            return false;

        string folder = Path.GetDirectoryName(filePath);
        string fileName = Path.GetFileName(filePath);

        var cycler = FindAnyObjectByType<SplatCycler>();
        if (cycler != null && !string.IsNullOrEmpty(folder))
        {
            cycler.splatFolder = folder;
            cycler.ScanFolder();
            for (int index = 0; index < cycler.Files.Count; index++)
            {
                if (Path.GetFileName(cycler.Files[index]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    cycler.SetIndex(index);
                    break;
                }
            }
        }

        var rig = FindAnyObjectByType<VRRig>();
        if (rig != null)
        {
            // Only place the rig once at startup; later file switches leave the
            // camera alone and let the splat transform carry over.
            rig.ResetToSpawnPoint(loader.targetRenderer);
        }

        Debug.Log($"[ViewerStartup] Auto-loaded launch file: {filePath}");
        return true;
    }
}