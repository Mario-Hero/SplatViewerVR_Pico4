using System.Collections.Generic;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Object = System.Object;
using UnityEngine.XR.OpenXR.Features.Interactions;
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using AOT;
using Unity.XR.PXR;


#if UNITY_EDITOR
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

#if AR_FOUNDATION
using UnityEngine.XR.ARSubsystems;
#endif

namespace Unity.XR.OpenXR.Features.PICOSupport
{
    /// <summary>
    /// Enables the PICO mobile OpenXR Loader for Android, and modifies the AndroidManifest to be compatible with Neo3.
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "PICO Support",
        Desc = "Necessary to deploy an PICO compatible app.",
        Company = "PICO",
        Version = SDKVersion,
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        CustomRuntimeLoaderBuildTargets = new[] { BuildTarget.Android },
        OpenxrExtensionStrings = PicoExtensionList,
        FeatureId = featureId
    )]
#endif

    [System.Serializable]
    public class PICOFeature : OpenXRFeature
    {
        public const string PicoExtensionList = "";
        public const string SDKVersion = "1.3.3";
        public static Action<bool> onAppFocusedAction;
        public bool isPicoSupport = false;
       


        /// <summary>
        /// The feature id string. This is used to give the feature a well known id for reference.
        /// </summary>
        public const string featureId = "com.unity.openxr.feature.pico";

        // Returns true if the feature is one of the PICO controller interaction profiles.
        // Uses the type name (string) instead of a direct type reference so this assembly
        // (Unity.XR.OpenXR.Features.PICOSupport) does not need a hard reference to the
        // Unity.XR.OpenXRPico assembly, avoiding a cross-assembly compile dependency.
        private static bool IsPicoControllerProfile(OpenXRFeature feature)
        {
            if (feature == null)
                return false;
            string name = feature.GetType().Name;
            return name == "PICONeo3ControllerProfile"
                || name == "PICO4UltraControllerProfile"
                || name == "PICO4ControllerProfile"
                || name == "PICOG3ControllerProfile";
        }

#if UNITY_EDITOR
        static AddRequest request;
        protected override void GetValidationChecks(List<ValidationRule> rules, BuildTargetGroup targetGroup)
        {
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            

            var AdditionalRules = new ValidationRule[]
            {
                new ValidationRule(this)
                {
                    message = "Only the PICO Touch Interaction Profile is supported right now.",
                    checkPredicate = () =>
                    {
                        if (null == settings)
                            return false;

                        bool touchFeatureEnabled = false;
                        bool otherInteractionFeatureEnabled = false;

                        foreach (var feature in settings.GetFeatures<OpenXRInteractionFeature>())
                        {
                            if (feature.enabled)
                            {
                                if (IsPicoControllerProfile(feature) ||
                                    (feature is KHRSimpleControllerProfile) || (feature is EyeGazeInteraction) ||
                                    (feature is HandInteractionProfile) || (feature is PalmPoseInteraction))
                                    touchFeatureEnabled = true;
                                else
                                    otherInteractionFeatureEnabled = true;
                            }
                        }

                        return touchFeatureEnabled && !otherInteractionFeatureEnabled;
                    },
                    fixIt = () =>
                    {
                        if (null == settings)
                            return;

                        foreach (var feature in settings.GetFeatures<OpenXRInteractionFeature>())
                        {
                            feature.enabled = IsPicoControllerProfile(feature);
                        }
                    },
                    error = true,
                }
            };

            rules.AddRange(AdditionalRules);
        }
        
        static void Progress()
        {
            if (request != null && request.IsCompleted)
            {
                if (request.Status == StatusCode.Success)
                    Debug.Log("Installed: " + request.Result.packageId);
                else if (request.Status >= StatusCode.Failure)
                    Debug.Log(request.Error.message);
                EditorApplication.update -= Progress;
                request = null;
            }
        }
        
        internal class PICOFeatureEditorWindow : EditorWindow
        {
            private Object feature;
            private Editor featureEditor;

            public static EditorWindow Create(Object feature)
            {
                var window = EditorWindow.GetWindow<PICOFeatureEditorWindow>(true, "PICO Feature Configuration", true);
                window.feature = feature;
                window.featureEditor = Editor.CreateEditor((UnityEngine.Object)feature);
                return window;
            }

            private void OnGUI()
            {
                featureEditor.OnInspectorGUI();
            }
        }

#endif
                                               
        
      
       

      



    }
}