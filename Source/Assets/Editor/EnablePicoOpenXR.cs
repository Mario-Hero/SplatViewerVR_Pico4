using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

/// <summary>
/// Forces the Android OpenXR configuration into the exact shape used by the
/// known-good PICO reference project (vr_player_unity6_double):
///
///   * PICOFeature            -> enabled (core Pico runtime support)
///   * OpenXRExtensions       -> enabled
///   * PICO 4 / 4 Ultra / Neo3 / G3 controller profiles -> enabled
///   * every other interaction profile (Meta / Oculus / Vive / ...) -> disabled
///   * MetaQuestFeature       -> disabled (it injects an Oculus-flavoured
///                               xrmanifest, which makes the headset treat the
///                               app as a Quest title and never start a Pico
///                               OpenXR session)
///
/// The order of enable/disable matters: Pico only accepts Pico interaction
/// profiles, and XR Management picks the AndroidManifest injected into
/// unityLibrary/xrmanifest.androidlib from the enabled feature set.
/// </summary>
[InitializeOnLoad]
public static class EnablePicoOpenXR
{
    static EnablePicoOpenXR()
    {
        // Defer until the editor is fully initialized so OpenXR settings exist.
        EditorApplication.delayCall += Enable;
    }

    [MenuItem("XR/Enable PICO OpenXR Features")]
    public static void Enable()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            settings = OpenXRSettings.Instance;
        if (settings == null)
        {
            Debug.LogError("[PicoSetup] No OpenXR settings object found.");
            return;
        }

        int changed = 0;

        // 1) Core Pico support must be ON, otherwise no Pico OpenXR session is created.
        TrySet<Unity.XR.OpenXR.Features.PICOSupport.PICOFeature>(settings, true, ref changed);
        TrySet<Unity.XR.OpenXR.Features.PICOSupport.OpenXRExtensions>(settings, true, ref changed);

        // 2) Pico touch controller profiles.
        TrySet<UnityEngine.XR.OpenXR.Features.Interactions.PICO4ControllerProfile>(settings, true, ref changed);
        TrySet<UnityEngine.XR.OpenXR.Features.Interactions.PICO4UltraControllerProfile>(settings, true, ref changed);
        TrySet<UnityEngine.XR.OpenXR.Features.Interactions.PICONeo3ControllerProfile>(settings, true, ref changed);
        TrySet<UnityEngine.XR.OpenXR.Features.Interactions.PICOG3ControllerProfile>(settings, true, ref changed);

        // 3) Any non-Pico interaction profile confuses runtime binding on Pico.
        foreach (var feature in settings.GetFeatures<OpenXRInteractionFeature>())
        {
            if (feature == null || !feature.enabled)
                continue;
            if (!IsPicoControllerProfile(feature))
            {
                feature.enabled = false;
                changed++;
                Debug.Log("[PicoSetup] Disabled non-Pico interaction profile: " + feature.GetType().Name);
            }
        }

        // 4) Meta Quest support injects a Quest-only XR manifest; keep it off on Pico.
        foreach (var feature in settings.GetFeatures<OpenXRFeature>())
        {
            if (feature == null || !feature.enabled)
                continue;
            if (feature.GetType().Name == "MetaQuestFeature")
            {
                feature.enabled = false;
                changed++;
                Debug.Log("[PicoSetup] Disabled MetaQuestFeature (Quest-only manifest).");
            }
        }

        // 5) Performance knobs. Both are pure runtime extensions — they do not
        //    inject a manifest and do not touch the splat data, so they can only
        //    make the frame cheaper, never change what is drawn.
        //    * FoveationFeature      -> fixed foveated rendering (peripheral
        //                               shading at a lower rate; the centre of
        //                               the view is untouched)
        //    * PerformanceSettings   -> lets the app ask for a higher CPU/GPU
        //                               performance level than the default
        TrySet<FoveationFeature>(settings, true, ref changed);
        TrySet<Unity.XR.OpenXR.Features.PICOSupport.PerformanceSettingsFeature>(settings, true, ref changed);
        ApplyPicoFoveation(true, 2);

        if (changed > 0)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("[PicoSetup] Applied " + changed + " PICO OpenXR change(s) for Android and saved settings.");
        }
        else
        {
            Debug.Log("[PicoSetup] PICO OpenXR features already in the expected state.");
        }
    }

    /// <summary>Human readable dump of the current Android OpenXR feature state.</summary>
    public static string Report()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            return "NO_SETTINGS";

        var sb = new System.Text.StringBuilder();
        sb.Append("renderMode=").Append(settings.renderMode).Append(" | ");
        foreach (var feature in settings.GetFeatures<OpenXRFeature>())
        {
            if (feature != null && feature.enabled)
                sb.Append(feature.GetType().Name).Append(',');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the runtime-level foveation options into PICOProjectSetting (the
    /// asset the Pico runtime reads when the OpenXR session is created).
    /// Subsampled layout stays off: it changes the swapchain layout and is the
    /// riskier of the two modes.
    /// </summary>
    public static void ApplyPicoFoveation(bool enable, int level)
    {
        var cfg = Unity.XR.OpenXR.Features.PICOSupport.PICOProjectSetting.GetProjectConfig();
        if (cfg == null)
        {
            Debug.LogWarning("[PicoSetup] PICOProjectSetting asset not found.");
            return;
        }

        var mode = FoveationFeature.FoveatedRenderingMode.FixedFoveatedRendering;
        var lvl = (FoveationFeature.FoveatedRenderingLevel)Mathf.Clamp(level, 0, 3);

        if (cfg.foveationEnable == enable &&
            cfg.foveatedRenderingMode == mode &&
            cfg.foveatedRenderingLevel == lvl &&
            !cfg.isSubsampledEnabled)
        {
            return;
        }

        cfg.foveationEnable = enable;
        cfg.foveatedRenderingMode = mode;
        cfg.foveatedRenderingLevel = lvl;
        cfg.isSubsampledEnabled = false;
        EditorUtility.SetDirty(cfg);
        AssetDatabase.SaveAssets();
        Debug.Log($"[PicoSetup] Foveation -> enable={enable}, mode={mode}, level={lvl}, subsampled=false");
    }

    static bool IsPicoControllerProfile(OpenXRFeature feature)
    {
        string name = feature.GetType().Name;
        return name == "PICONeo3ControllerProfile"
            || name == "PICO4UltraControllerProfile"
            || name == "PICO4ControllerProfile"
            || name == "PICOG3ControllerProfile";
    }

    private static void TrySet<T>(OpenXRSettings settings, bool value, ref int changed) where T : OpenXRFeature
    {
        var feature = settings.GetFeature<T>();
        if (feature == null)
        {
            Debug.LogWarning("[PicoSetup] Feature not found: " + typeof(T).FullName);
            return;
        }
        if (feature.enabled != value)
        {
            feature.enabled = value;
            changed++;
            Debug.Log("[PicoSetup] " + (value ? "Enabled" : "Disabled") + " feature: " + typeof(T).FullName);
        }
    }
}
