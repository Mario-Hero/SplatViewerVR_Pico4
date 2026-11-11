// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Viewer-page controls for the VR Gaussian Splatting viewer.
///
/// IMPORTANT: these controls NEVER move the XR rig / camera. Every stick gesture
/// translates or rotates the splat GameObject instead, so the headset keeps its
/// real-world position and the splat transform simply carries over to whatever
/// file gets loaded next.
///
/// Both controllers are interchangeable — every gesture works on the left OR the
/// right hand, and mixing them (stick on one hand, button on the other) also works.
///
/// Viewer page (file browser closed):
///   Stick Y                      → zoom: the splat slides towards / away from you
///   Stick X                      → previous / next splat file in the folder
///   Trigger (hold) + Stick       → move the splat: stick X slides it sideways,
///                                  stick Y lifts it up / lowers it down
///   Grip (hold) + swing the hand → orbit the splat around the camera (1:1 with
///                                  the controller rotation, like grabbing the
///                                  world and turning it)
///   Stick click (short press)    → reset the splat rotation
///   Stick click (hold ~0.6s)     → reset the splat position
///   B (either hand)              → open the file browser
///
/// Splat position and rotation are inherited when switching files: loading only
/// swaps the mesh data, nothing resets the transform.
///
/// Keyboard fallback (editor / desktop, no HMD):
///   W / A / S / D      → move the splat forward / left / back / right
///   Space / C          → move the splat up / down
///   Mouse wheel        → zoom
///   R / F              → next / previous splat
///   X / Home           → reset splat rotation / position
///   Mouse drag (RMB)   → look around (editor convenience only)
/// </summary>
[RequireComponent(typeof(VRRig))]
public class VRLocomotion : MonoBehaviour
{
    [Header("Splat Movement")]
    [Tooltip("Pan speed in metres per second for the trigger + stick gesture.")]
    public float moveSpeed = 2.0f;

    [Header("Splat Zoom (plain stick up / down)")]
    [Tooltip("Fraction of the eye→splat distance covered per second at full stick. " +
             "0.7 roughly halves the distance in a second.")]
    public float zoomRate = 0.7f;

    [Tooltip("Lower bound of the zoom speed (m/s) so zooming still moves when very close.")]
    public float zoomMinSpeed = 0.25f;

    [Tooltip("Upper bound of the zoom speed (m/s) so zooming stays controllable far away.")]
    public float zoomMaxSpeed = 4f;

    [Tooltip("Closest the splat centre may get to your eyes (metres).")]
    public float zoomMinDistance = 0.2f;

    [Tooltip("Furthest the splat centre may be pushed away (metres).")]
    public float zoomMaxDistance = 60f;

    [Header("Grip Grab-Orbit")]
    [Tooltip("Multiplier on the controller rotation while gripping. 1 = 1:1 with your hand.")]
    [Range(0.2f, 3f)]
    public float grabRotateSpeed = 1f;

    [Tooltip("Controller rotation (degrees) below which the delta is treated as hand jitter.")]
    public float grabRotationDeadzone = 0.05f;

    [Tooltip("On: the splat also orbits around your head (turning the whole scene). " +
             "Off: it only spins in place around its own centre.")]
    public bool grabOrbitAroundCamera = true;

    [Tooltip("Analogue stick dead-zone radius (0–1).")]
    [Range(0f, 0.5f)]
    public float stickDeadzone = 0.2f;

    [Header("Stick Click")]
    [Tooltip("Seconds the stick has to stay pressed to reset the splat position.")]
    public float longPressSeconds = 0.6f;

    [Tooltip("Stick deflection above which a click is ignored (avoids resets while moving).")]
    [Range(0f, 1f)]
    public float clickDeflectionLimit = 0.6f;

    [Header("File Cycling")]
    [Tooltip("Stick X deflection needed to switch to the next / previous splat.")]
    [Range(0.2f, 1f)]
    public float cycleThreshold = 0.7f;

    [Tooltip("Minimum seconds between two splat switches.")]
    public float cycleRepeatDelay = 0.35f;

    [Header("Mouse Look (desktop fallback)")]
    [Tooltip("Mouse look sensitivity when using the keyboard fallback.")]
    public float mouseSensitivity = 2f;

    [Tooltip("Hide and lock the cursor while using desktop mode.")]
    public bool lockCursorInDesktopMode = true;

    // ── Private state ─────────────────────────────────────────────────────────

    VRRig  _rig;
    float  _mousePitch;   // accumulated vertical mouse look (desktop only)
    VRFileBrowser _browser;
    VROptionsMenu _optionsMenu;
    SmbSettingsPanel _smbPanel;
    SplatCycler _cycler;

    bool  _clickHeld;
    float _clickTime;
    bool  _longPressFired;
    float _cycleCD;

    // Grip grab-orbit state. While a grip is held we track that controller's
    // world rotation and apply the per-frame delta around the camera, which
    // reads as "grab the world and turn it".
    bool       _grabbing;
    XRNode     _grabNode;
    Quaternion _grabLastRotation;

    // Splat transform state. The authored pose is the "home" the click gestures
    // restore; anything the user does is plain transform editing and therefore
    // survives a file switch untouched.
    Transform  _splat;
    Vector3    _homePosition;
    Quaternion _homeRotation;
    bool       _homeCaptured;

    bool AnyUiOpen =>
        (_browser != null && _browser.IsOpen) ||
        (_optionsMenu != null && _optionsMenu.IsOpen) ||
        (_smbPanel != null && _smbPanel.IsOpen);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        _rig = GetComponent<VRRig>();
        _browser = FindAnyObjectByType<VRFileBrowser>();
        _optionsMenu = FindAnyObjectByType<VROptionsMenu>();
        _cycler = FindAnyObjectByType<SplatCycler>();

        if (XRSettings.isDeviceActive)
            SyncDesktopPitchFromCamera();
        else
            ResetDesktopLook();
    }

    void SyncDesktopPitchFromCamera()
    {
        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
        {
            float pitch = cam.transform.localEulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;
            _mousePitch = pitch;
        }
    }

    public void ResetDesktopLook()
    {
        if (XRSettings.isDeviceActive)
            return;

        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
            cam.transform.localRotation = Quaternion.identity;

        _mousePitch = 0f;
    }

    void Update()
    {
        if (XRSettings.isDeviceActive)
            VRInput();
        else
            KeyboardMouseFallback();
    }

    // ── Splat transform access ────────────────────────────────────────────────

    /// <summary>
    /// Transform of the splat currently being displayed. Its authored pose is
    /// captured once, and the click gestures use it to move / turn it back.
    /// </summary>
    Transform SplatTransform()
    {
        if (_splat != null)
            return _splat;

        GaussianSplatRenderer renderer = GetRenderer();
        if (renderer == null)
            return null;

        _splat = renderer.transform;
        _homePosition = _splat.localPosition;
        _homeRotation = _splat.localRotation;
        _homeCaptured = true;
        Debug.Log($"[VRLocomotion] Splat transform captured at {_homePosition}");
        return _splat;
    }

    /// <summary>World-space centre of the currently loaded splat asset.</summary>
    static Vector3 SplatCenter(Transform splat)
    {
        var renderer = splat.GetComponent<GaussianSplatRenderer>();
        if (renderer == null || renderer.m_Asset == null)
            return splat.position;

        Vector3 centerLocal = (renderer.m_Asset.boundsMin + renderer.m_Asset.boundsMax) * 0.5f;
        return splat.TransformPoint(centerLocal);
    }

    Camera ViewCamera => _rig != null && _rig.xrCamera != null ? _rig.xrCamera : Camera.main;

    // ── VR input ──────────────────────────────────────────────────────────────

    void VRInput()
    {
        if (_optionsMenu == null) _optionsMenu = FindAnyObjectByType<VROptionsMenu>();
        if (_smbPanel == null) _smbPanel = FindAnyObjectByType<SmbSettingsPanel>();
        if (_browser == null) _browser = FindAnyObjectByType<VRFileBrowser>();

        if (AnyUiOpen)
        {
            _grabbing = false;
            ResetClickState();
            return;
        }

        Transform splat = SplatTransform();
        if (splat == null || !_homeCaptured)
            return;

        Vector2 stick = ReadStickAny();
        bool trigger = ReadTriggerAny();

        // Grip held on either hand → grab-orbit. The stick is ignored while
        // gripping, so it can never fight the orbit.
        if (TryGetGripHand(out XRNode gripNode))
        {
            // Both grips open the options menu (VROptionsMenu), so don't orbit
            // during that transition.
            bool bothGrips = IsGripHeld(XRNode.LeftHand) && IsGripHeld(XRNode.RightHand);
            if (bothGrips || !GrabOrbit(splat, gripNode))
            {
                _grabbing = false;
                ResetClickState();
            }
            return;
        }

        _grabbing = false;

        // Joystick click gestures (rotation reset / position reset).
        HandleStickClick(splat, stick);

        if (trigger)
        {
            PanSplat(splat, stick);
            return;
        }

        ZoomSplat(splat, stick);
        CycleFile(stick);
    }

    /// <summary>
    /// Grab-orbit: while the grip stays held, every degree the controller turns
    /// is applied to the splat around the camera position — the content orbits
    /// around you and stays at the same distance, exactly like turning a
    /// turntable the camera is sitting on.
    /// </summary>
    bool GrabOrbit(Transform splat, XRNode node)
    {
        if (!TryGetControllerRotation(node, out Quaternion worldRotation))
        {
            _grabbing = false;
            return false;
        }

        if (!_grabbing || node != _grabNode)
        {
            _grabbing = true;
            _grabNode = node;
            _grabLastRotation = worldRotation;
            return true;
        }

        Quaternion delta = worldRotation * Quaternion.Inverse(_grabLastRotation);
        _grabLastRotation = worldRotation;

        float angle = Quaternion.Angle(Quaternion.identity, delta);
        if (angle < grabRotationDeadzone)
            return true;

        if (!Mathf.Approximately(grabRotateSpeed, 1f))
        {
            // Scale the rotation angle (supports multipliers above 1, which a
            // clamped Slerp could not do).
            delta.ToAngleAxis(out float degrees, out Vector3 axis);
            if (degrees > 180f) degrees -= 360f;
            if (axis.sqrMagnitude < 0.5f) return true;
            delta = Quaternion.AngleAxis(degrees * grabRotateSpeed, axis);
        }

        splat.rotation = delta * splat.rotation;

        if (grabOrbitAroundCamera)
        {
            Camera cam = ViewCamera;
            Vector3 pivot = cam != null ? cam.transform.position : splat.position;
            // Swing the pivot around your head so the whole scene orbits instead
            // of merely spinning on its own axis.
            splat.position = pivot + delta * (splat.position - pivot);
        }

        return true;
    }

    void ResetClickState()
    {
        _clickHeld = false;
        _clickTime = 0f;
        _longPressFired = false;
    }

    void HandleStickClick(Transform splat, Vector2 stick)
    {
        bool held = ReadStickClickAny();
        if (stick.magnitude > clickDeflectionLimit)
            held = false;

        if (held)
        {
            if (!_clickHeld)
            {
                _clickHeld = true;
                _clickTime = 0f;
                _longPressFired = false;
            }
            else
            {
                _clickTime += Time.deltaTime;
                if (!_longPressFired && _clickTime >= longPressSeconds)
                {
                    _longPressFired = true;
                    splat.localPosition = _homePosition;
                    Debug.Log("[VRLocomotion] Long press → splat position reset");
                }
            }
        }
        else if (_clickHeld)
        {
            if (!_longPressFired && _clickTime > 0f)
            {
                splat.localRotation = _homeRotation;
                Debug.Log("[VRLocomotion] Click → splat rotation reset");
            }
            ResetClickState();
        }
    }

    /// <summary>
    /// Trigger + stick moves the splat. Stick Y lifts it straight up / down;
    /// stick X slides it sideways on the camera's horizontal axis. The sideways
    /// sign is inverted on purpose (pushing right slides the content left, which
    /// reads as "I stepped right"), the vertical sign is direct (up = up).
    /// </summary>
    void PanSplat(Transform splat, Vector2 stick)
    {
        if (stick.magnitude <= stickDeadzone)
            return;

        Vector3 right = Vector3.right;
        Camera cam = ViewCamera;
        if (cam != null)
        {
            right = cam.transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f)
                right = Vector3.right;
            right.Normalize();
        }

        Vector3 horizontal = right * -stick.x;
        Vector3 vertical   = Vector3.up * stick.y;

        splat.position += (horizontal + vertical) * (moveSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Plain stick Y = zoom. The splat slides along the line between your eyes
    /// and its centre: up pulls it closer (the picture grows), down pushes it
    /// away (the picture shrinks). The camera stays exactly where it is, and the
    /// step scales with the current distance so the perceived zoom speed stays
    /// constant whether the splat is 0.5 m or 20 m away.
    /// </summary>
    void ZoomSplat(Transform splat, Vector2 stick)
    {
        float axis = stick.y;
        if (Mathf.Abs(axis) <= stickDeadzone)
            return;

        Camera cam = ViewCamera;
        if (cam == null) return;

        Vector3 eye = cam.transform.position;
        Vector3 center = SplatCenter(splat);
        Vector3 toCenter = center - eye;
        float distance = toCenter.magnitude;
        if (distance < 0.0001f)
            return;

        // Speed in m/s: proportional to the distance, clamped to a usable band.
        float speed = Mathf.Clamp(distance * zoomRate, zoomMinSpeed, zoomMaxSpeed);
        float step = axis * speed * Time.deltaTime;

        float newDistance = Mathf.Clamp(distance - step, zoomMinDistance, zoomMaxDistance);
        step = distance - newDistance;

        splat.position -= toCenter.normalized * step;
    }

    void CycleFile(Vector2 stick)
    {
        if (_cycler == null) _cycler = FindAnyObjectByType<SplatCycler>();
        if (_cycler == null || _cycler.IsMoviePlaying) return;

        _cycleCD -= Time.deltaTime;
        if (Mathf.Abs(stick.x) < cycleThreshold || _cycleCD > 0f)
        {
            if (Mathf.Abs(stick.x) < cycleThreshold * 0.6f)
                _cycleCD = 0f;
            return;
        }

        if (stick.x > 0f) _cycler.LoadNext();
        else              _cycler.LoadPrevious();
        _cycleCD = cycleRepeatDelay;
    }

    static GaussianSplatRenderer GetRenderer()
    {
        var loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (loader != null && loader.targetRenderer != null)
            return loader.targetRenderer;
        return FindAnyObjectByType<GaussianSplatRenderer>();
    }

    // ── Keyboard / mouse fallback ─────────────────────────────────────────────

    void KeyboardMouseFallback()
    {
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame))
            return;
        if (_optionsMenu == null) _optionsMenu = FindAnyObjectByType<VROptionsMenu>();
        if (_optionsMenu != null && _optionsMenu.IsOpen)
            return;

        UpdateDesktopCursorState();

        Transform splat = SplatTransform();
        if (splat == null || !_homeCaptured)
            return;

        // Translation — moves the splat, never the camera.
        float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float y = (Input.GetKey(KeyCode.Space) ? 1f : 0f) - (Input.GetKey(KeyCode.C) ? 1f : 0f);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 right = transform.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.right;
        right.Normalize();

        Vector3 move = (right * h + forward * v + Vector3.up * y);
        if (move.sqrMagnitude > 0.01f)
            splat.position -= move.normalized * (moveSpeed * Time.deltaTime);

        // Mouse wheel zooms the splat, same as the stick Y axis.
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.01f)
        {
            Camera cam = ViewCamera;
            if (cam != null)
            {
                Vector3 toCenter = SplatCenter(splat) - cam.transform.position;
                if (toCenter.sqrMagnitude > 0.0001f)
                    splat.position -= toCenter.normalized * (Mathf.Sign(wheel) * 0.1f);
            }
        }

        // Reset shortcuts, handy when testing in the editor.
        if (Input.GetKeyDown(KeyCode.X))    splat.localRotation = _homeRotation;
        if (Input.GetKeyDown(KeyCode.Home)) splat.localPosition = _homePosition;

        bool allowMouseLook = !lockCursorInDesktopMode || Cursor.lockState == CursorLockMode.Locked;
        if (allowMouseLook)
        {
            Camera cam = ViewCamera;
            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            transform.Rotate(0f, mouseX, 0f, Space.World);

            _mousePitch -= mouseY;
            _mousePitch  = Mathf.Clamp(_mousePitch, -80f, 80f);

            if (cam != null)
            {
                Vector3 euler = cam.transform.localEulerAngles;
                euler.x = _mousePitch;
                cam.transform.localEulerAngles = euler;
            }
        }
    }

    void UpdateDesktopCursorState()
    {
        if (!lockCursorInDesktopMode)
            return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.visible = false;
        }
    }

    // ── XR input helpers (both hands are interchangeable) ─────────────────────

    static Vector2 ReadStick(XRNode node)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 &&
            devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v;
        return Vector2.zero;
    }

    /// <summary>Stick value of whichever hand is pushing harder.</summary>
    static Vector2 ReadStickAny()
    {
        Vector2 left  = ReadStick(XRNode.LeftHand);
        Vector2 right = ReadStick(XRNode.RightHand);
        return left.sqrMagnitude >= right.sqrMagnitude ? left : right;
    }

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static float ReadAxis1D(XRNode node, InputFeatureUsage<float> usage)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(usage, out float v))
            return v;
        return 0f;
    }

    static bool ReadTriggerAny()
    {
        return ReadAxis1D(XRNode.LeftHand, CommonUsages.trigger) > 0.5f
            || ReadAxis1D(XRNode.RightHand, CommonUsages.trigger) > 0.5f
            || ReadButton(XRNode.LeftHand, CommonUsages.triggerButton)
            || ReadButton(XRNode.RightHand, CommonUsages.triggerButton);
    }

    static bool IsGripHeld(XRNode node)
    {
        return ReadAxis1D(node, CommonUsages.grip) > 0.5f;
    }

    /// <summary>Hand whose grip is squeezed hardest, if any grip is held.</summary>
    static bool TryGetGripHand(out XRNode node)
    {
        float left  = ReadAxis1D(XRNode.LeftHand, CommonUsages.grip);
        float right = ReadAxis1D(XRNode.RightHand, CommonUsages.grip);

        if (left <= 0.5f && right <= 0.5f)
        {
            node = XRNode.RightHand;
            return false;
        }

        node = right >= left ? XRNode.RightHand : XRNode.LeftHand;
        return true;
    }

    /// <summary>
    /// Controller rotation in world space (tracking space is relative to the XR
    /// rig, so the rig rotation is folded in).
    /// </summary>
    bool TryGetControllerRotation(XRNode node, out Quaternion worldRotation)
    {
        worldRotation = Quaternion.identity;

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count == 0)
            return false;

        if (!devices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion local))
            return false;

        worldRotation = (_rig != null ? _rig.transform.rotation : Quaternion.identity) * local;
        return true;
    }

    static bool ReadStickClickAny()
    {
        return ReadButton(XRNode.LeftHand, CommonUsages.primary2DAxisClick)
            || ReadButton(XRNode.RightHand, CommonUsages.primary2DAxisClick);
    }
}
