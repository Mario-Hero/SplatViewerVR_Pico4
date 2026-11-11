// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Startup orientation for the GaussianSplat GameObject plus desktop-only
/// rotation shortcuts. Attach this to the same GameObject that has
/// GaussianSplatRenderer.
///
/// VR rotation is NOT handled here: holding a grip and swinging the controller
/// is the grab-orbit gesture in <see cref="VRLocomotion"/>, which rotates the
/// splat around the camera. The old "grip + stick" rotation has been removed.
///
/// Desktop controls:
///   Q / E                → rotate around Y axis
///   Arrow Left / Right   → rotate around Y axis
///   Arrow Up / Down      → rotate around X axis
///   , / .                → rotate around Z axis
///   Home                 → reset to original rotation
///   End                  → flip upside down
/// </summary>
public class SplatRotator : MonoBehaviour
{
    [Header("Rotation Speed")]
    [Tooltip("Degrees per second when holding a key (desktop only).")]
    public float rotationSpeed = 90f;

    [Header("Saved Rotation")]
    [Tooltip("Rotation applied at startup. Set this in the Inspector to bake a corrected orientation.")]
    public Vector3 startEuler = Vector3.zero;

    // ── Private ───────────────────────────────────────────────────────────────

    Quaternion _originalRotation;
    VRFileBrowser _browser;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        transform.localEulerAngles = startEuler;
        _originalRotation = transform.localRotation;
        _browser = FindAnyObjectByType<VRFileBrowser>();
    }

    void Update()
    {
        // Headset: rotation comes from the grip grab-orbit gesture in VRLocomotion.
        if (XRSettings.isDeviceActive)
            return;

        KeyboardRotate();
    }

    // ── Keyboard rotation ─────────────────────────────────────────────────────

    void KeyboardRotate()
    {
        if (_browser != null && _browser.IsOpen)
            return;

        float dt = rotationSpeed * Time.deltaTime;

        if (Input.GetKey(KeyCode.Q))          transform.Rotate(Vector3.up,    -dt, Space.World);
        if (Input.GetKey(KeyCode.E))          transform.Rotate(Vector3.up,     dt, Space.World);
        if (Input.GetKey(KeyCode.LeftArrow))  transform.Rotate(Vector3.up,    -dt, Space.World);
        if (Input.GetKey(KeyCode.RightArrow)) transform.Rotate(Vector3.up,     dt, Space.World);
        if (Input.GetKey(KeyCode.UpArrow))    transform.Rotate(Vector3.right,  -dt, Space.World);
        if (Input.GetKey(KeyCode.DownArrow))  transform.Rotate(Vector3.right,   dt, Space.World);
        if (Input.GetKey(KeyCode.Comma))      transform.Rotate(Vector3.forward, -dt, Space.World);
        if (Input.GetKey(KeyCode.Period))     transform.Rotate(Vector3.forward,  dt, Space.World);

        if (Input.GetKeyDown(KeyCode.End))  FlipUpsideDown();
        if (Input.GetKeyDown(KeyCode.Home)) ResetRotation();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Rotates 180° around the world X axis — fixes most upside-down splats.</summary>
    [ContextMenu("Flip Upside Down")]
    public void FlipUpsideDown()
    {
        transform.Rotate(Vector3.right, 180f, Space.World);
        Debug.Log($"[SplatRotator] Flipped. Euler now: {transform.localEulerAngles}");
    }

    /// <summary>Resets to the rotation stored in startEuler.</summary>
    [ContextMenu("Reset Rotation")]
    public void ResetRotation()
    {
        transform.localRotation = _originalRotation;
        Debug.Log("[SplatRotator] Rotation reset.");
    }

    /// <summary>
    /// Saves the current rotation into startEuler so it persists across Play sessions.
    /// Call this from the Inspector context menu after dialling in the right orientation.
    /// </summary>
    [ContextMenu("Save Current Rotation as Default")]
    public void SaveCurrentRotation()
    {
        startEuler = transform.localEulerAngles;
        Debug.Log($"[SplatRotator] Saved rotation: {startEuler}");
    }
}
