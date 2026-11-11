// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Build-time safeguard for Pico OS launcher visibility.
///
/// Unity merges AndroidManifest.xml from several sources (the main manifest,
/// XR Management's xrmanifest, OpenXR feature hooks). Depending on merge
/// priority, the Pico markers declared in Assets/Plugins/Android can end up on
/// a different intent-filter than the MAIN/LAUNCHER one. This hook patches the
/// final merged Gradle manifest so that:
///   * the MAIN activity has a MAIN/LAUNCHER intent-filter,
///   * that filter also carries com.picovr.intent.category.VR and the Khronos
///     IMMERSIVE_HMD category, while keeping com.oculus.intent.category.VR,
///   * the application declares pvr.app.type=vr,
///   * the activity is exported (required from Android 12 / targetSdk 31).
///
/// All operations are additive and idempotent, so Quest support is preserved.
/// Mirrors the approach used by the PICO OpenXR package's build hook.
/// </summary>
public sealed class PicoLauncherManifestHook : IPostGenerateGradleAndroidProject
{
    const string AndroidNs = "http://schemas.android.com/apk/res/android";
    const string MainAction = "android.intent.action.MAIN";
    const string LauncherCategory = "android.intent.category.LAUNCHER";
    const string OculusVrCategory = "com.oculus.intent.category.VR";
    const string PicoVrCategory = "com.picovr.intent.category.VR";
    const string ImmersiveHmdCategory = "org.khronos.openxr.intent.category.IMMERSIVE_HMD";

    public int callbackOrder => 100; // after XR / OpenXR feature hooks

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        foreach (string manifest in FindManifests(path))
            PatchManifest(manifest);
    }

    static List<string> FindManifests(string root)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return result;
        Walk(root, 0, result);
        return result;
    }

    static void Walk(string dir, int depth, List<string> result)
    {
        if (depth > 4) return;

        string candidate = Path.Combine(dir, "src", "main", "AndroidManifest.xml");
        if (File.Exists(candidate)) result.Add(candidate);

        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".gradle", StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(sub, depth + 1, result);
            }
        }
        catch (Exception) { /* inaccessible folders are irrelevant */ }
    }

    static void PatchManifest(string file)
    {
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(file);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("android", AndroidNs);

            XmlNode manifest = doc.SelectSingleNode("/manifest");
            XmlNode application = doc.SelectSingleNode("/manifest/application");
            if (application == null) return;

            // Pico: declare hand-tracking support so the system does not intercept
            // launch with com.pvr.vrdisplay.HandDialogActivity (looks like a stuck
            // loading screen). Mirrors the official Pico VR app setup.
            bool changed = EnsurePermission(doc, manifest, "com.picovr.permission.HAND_TRACKING");
            changed |= EnsureMetaData(doc, application, "pvr.app.type", "vr");
            changed |= EnsureMetaData(doc, application, "handtracking", "1");

            XmlNodeList activities = application.SelectNodes("activity");
            XmlNode mainActivity = null;
            XmlNode mainFilter = null;

            foreach (XmlNode activity in activities)
            {
                foreach (XmlNode filter in activity.SelectNodes("intent-filter"))
                {
                    if (filter.SelectSingleNode("action[@android:name='" + MainAction + "']", ns) == null)
                        continue;
                    mainActivity = activity;
                    mainFilter = filter;
                    break;
                }
                if (mainFilter != null) break;
            }

            // Some XR configurations declare the Unity player activity without
            // any intent-filter. Recreate one when that happens.
            if (mainActivity == null && activities.Count > 0)
            {
                foreach (XmlNode activity in activities)
                {
                    XmlAttribute nameAttr = activity.Attributes?["name", AndroidNs];
                    if (nameAttr == null) continue;
                    if (nameAttr.Value == "com.unity3d.player.UnityPlayerActivity" ||
                        nameAttr.Value == "com.unity3d.player.UnityPlayerGameActivity")
                    {
                        mainActivity = activity;
                        break;
                    }
                }
                if (mainActivity == null) mainActivity = activities[0];
            }

            if (mainActivity == null) return;

            XmlAttribute exported = mainActivity.Attributes?["exported", AndroidNs];
            if (exported == null)
            {
                exported = doc.CreateAttribute("android", "exported", AndroidNs);
                exported.Value = "true";
                mainActivity.Attributes.Append(exported);
                changed = true;
            }
            else if (!string.Equals(exported.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                exported.Value = "true";
                changed = true;
            }

            if (mainFilter == null)
            {
                mainFilter = doc.CreateElement("intent-filter");
                XmlElement action = doc.CreateElement("action");
                XmlAttribute actionName = doc.CreateAttribute("android", "name", AndroidNs);
                actionName.Value = MainAction;
                action.Attributes.Append(actionName);
                mainFilter.AppendChild(action);
                mainActivity.AppendChild(mainFilter);
                changed = true;
            }

            changed |= EnsureCategory(doc, mainFilter, LauncherCategory);
            changed |= EnsureCategory(doc, mainFilter, OculusVrCategory);
            changed |= EnsureCategory(doc, mainFilter, PicoVrCategory);
            changed |= EnsureCategory(doc, mainFilter, ImmersiveHmdCategory);

            if (changed)
            {
                doc.Save(file);
                Debug.Log("[PicoLauncher] Patched merged manifest: " + file);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PicoLauncher] Could not patch " + file + ": " + ex.Message);
        }
    }

    static bool EnsureCategory(XmlDocument doc, XmlNode filter, string name)
    {
        foreach (XmlNode node in filter.SelectNodes("category"))
        {
            XmlAttribute attr = node.Attributes?["name", AndroidNs];
            if (attr != null && attr.Value == name) return false;
        }

        XmlElement category = doc.CreateElement("category");
        XmlAttribute categoryName = doc.CreateAttribute("android", "name", AndroidNs);
        categoryName.Value = name;
        category.Attributes.Append(categoryName);
        filter.AppendChild(category);
        return true;
    }

    static bool EnsurePermission(XmlDocument doc, XmlNode manifest, string name)
    {
        foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
        {
            XmlAttribute attr = node.Attributes?["name", AndroidNs];
            if (attr != null && attr.Value == name) return false;
        }

        XmlElement permission = doc.CreateElement("uses-permission");
        XmlAttribute permName = doc.CreateAttribute("android", "name", AndroidNs);
        permName.Value = name;
        permission.Attributes.Append(permName);
        manifest.AppendChild(permission);
        return true;
    }

    static bool EnsureMetaData(XmlDocument doc, XmlNode application, string name, string value)
    {
        foreach (XmlNode node in application.SelectNodes("meta-data"))
        {
            XmlAttribute nameAttr = node.Attributes?["name", AndroidNs];
            if (nameAttr == null || nameAttr.Value != name) continue;

            XmlAttribute valueAttr = node.Attributes["value", AndroidNs];
            if (valueAttr != null && valueAttr.Value == value) return false;
            if (valueAttr != null)
            {
                valueAttr.Value = value;
                return true;
            }
        }

        XmlElement metaData = doc.CreateElement("meta-data");
        XmlAttribute metaName = doc.CreateAttribute("android", "name", AndroidNs);
        metaName.Value = name;
        XmlAttribute metaValue = doc.CreateAttribute("android", "value", AndroidNs);
        metaValue.Value = value;
        metaData.Attributes.Append(metaName);
        metaData.Attributes.Append(metaValue);
        application.AppendChild(metaData);
        return true;
    }
}
#endif
