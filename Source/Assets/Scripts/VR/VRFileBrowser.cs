// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// World-space VR file browser for browsing the file system and loading .ply/.spz/.sog/.spx splat files.
///
/// VR Controls (both controllers are interchangeable):
///   B                          → from the viewer: open the browser;
///                                inside the browser: parent folder
///                                (closes the browser at the top folder)
///   Trigger or A               → select (enter folder / load file)
///   Trigger + B                → toggle preloading
///   Stick up / down            → navigate the list
///
/// Desktop fallback:
///   Esc / Tab  → toggle browser
///   Arrow keys → navigate list
///   Enter      → select
///   Backspace  → go to parent
/// </summary>
public class VRFileBrowser : MonoBehaviour
{
    const float FpsUpdateInterval = 0.25f;
    const int HelpPanelWidth = 380;
    const int HelpPanelHeight = 620;
    const int BrowserPreloadAheadCount = 2;

    [Header("Setup")]
    [Tooltip("RuntimeSplatLoader to load selected files into.")]
    public RuntimeSplatLoader loader;

    [Tooltip("Optional SplatCycler — its folder will be updated when a file is loaded from the browser.")]
    public SplatCycler cycler;

    [Tooltip("Initial folder path. Leave empty to start at drive list.")]
    public string startPath = "";

    [Header("Placement")]
    [Tooltip("Distance in front of the user when the browser opens.")]
    public float spawnDistance = 1.5f;

    /// <summary>True when the browser panel is visible and consuming right-hand input.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Remains true for one extra frame after closing, to prevent button leak to other scripts.</summary>
    public bool WasOpenThisFrame { get; private set; }

    // ── Layout constants ──────────────────────────────────────────────────────

    const int CW = 900, CH = 650;
    const float SCALE = 0.001f;
    const int ROWS = 14;
    const int ROW_H = 38;
    const int PATH_H = 36;
    const int HINT_H = 30;
    const int PAD = 10;
    const int FONT_ENTRY = 22;
    const int FONT_PATH = 18;
    const int FONT_HINT = 16;

    // ── Colors ────────────────────────────────────────────────────────────────

    static readonly Color COL_BG      = new Color(0.08f, 0.08f, 0.10f, 0.96f);
    static readonly Color COL_SEL     = new Color(0.20f, 0.40f, 0.85f, 0.80f);
    static readonly Color COL_CURRENT = new Color(0.16f, 0.48f, 0.22f, 0.65f);
    static readonly Color COL_ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color COL_DIR     = new Color(1f, 0.88f, 0.40f);
    static readonly Color COL_SETUP   = new Color(0.45f, 0.90f, 1f);
    static readonly Color COL_FILE    = Color.white;
    static readonly Color COL_PATH    = new Color(0.65f, 0.65f, 0.65f);
    static readonly Color COL_HINT    = new Color(0.45f, 0.45f, 0.45f);
    static readonly Color COL_CLEAR   = new Color(0f, 0f, 0f, 0f);

    // ── State ─────────────────────────────────────────────────────────────────

    string _currentPath;
    string _rootPath;
    readonly List<Entry> _entries = new List<Entry>();
    int _sel;
    int _scroll;

    // ── UI objects ────────────────────────────────────────────────────────────

    GameObject _root;
    Text _pathText;
    Text _hintText;
    Text _helpText;
    Text[] _rowTexts;
    Image[] _rowBgs;
    static Font _font;

    // ── Input state ───────────────────────────────────────────────────────────

    float _stickCD;
    bool _trigReady = true;
    bool _backReady = true;
    bool _preloadToggleReady = true;
    bool _comboActive;      // Trigger + B was used: swallow the trigger until release
    float _fpsAdjustCD;
    VROptionsMenu _optMenu;
    SmbSettingsPanel _smbPanel;

    // Movie mode
    enum MovieState { Idle, Loading, Playing }
    MovieState _movieState = MovieState.Idle;
    int _movieLoadedCount;
    int _movieTotalCount;
    float _fpsAccumTime;
    int _fpsAccumFrames;
    float _displayFps;
    string _lastBrowseError;
    string _libraryHint;
    string _browseStatus;
    Task<List<SplatEntry>> _browseTask;
    int _browseGeneration;
    int _browseTaskGeneration;
    Task<bool> _downloadTask;
    Entry _downloadEntry;
    string _downloadStatus;
    string _lastHighlightedFilePath;
    string _lastPreloadFolderPath;
    string _lastPreloadAnchorPath;
    bool _lastPreloadEnabled;

    struct Entry
    {
        public string name;
        public string path;
        public bool isDir;
        public bool isSmbSetup;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (loader == null) loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (cycler == null) cycler = FindAnyObjectByType<SplatCycler>();

        SplatFileSystem.Initialize();
        _rootPath = SplatFileSystem.LocalBrowseRoot;
        // Files land in the public Downloads folder when they are copied over USB
        // or downloaded on the headset, so that is the default landing folder.
        if (string.IsNullOrEmpty(startPath))
        {
            string preferred = QuestSplatStorage.GetDefaultBrowseFolder();
            _currentPath = CanListFolder(preferred) ? preferred : _rootPath;
        }
        else
        {
            _currentPath = startPath;
        }
        if (!string.IsNullOrEmpty(_currentPath) && !QuestSplatStorage.IsInsideRoot(_currentPath))
            _currentPath = _rootPath;

        // Cache a font reference
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);

        BuildUI();
        _root.SetActive(false);
    }

    void Update()
    {
        UpdateFpsCounter();
        PumpBrowse();
        PumpDownload();

        // Clear the one-frame guard from previous frame
        WasOpenThisFrame = IsOpen;

        // Movie loading pump — runs even when browser is closed
        if (_movieState == MovieState.Loading)
        {
            bool done = cycler != null && cycler.PumpMovieLoad();
            if (done)
            {
                if (cycler != null && cycler.IsMovieReady)
                {
                    _movieState = MovieState.Playing;
                    cycler.StartMoviePlayback();
                    Debug.Log("[VRFileBrowser] Movie loading complete — playback started");
                    if (IsOpen) ToggleBrowser(); // close browser when playback starts
                }
                else
                {
                    _movieState = MovieState.Idle;
                    Debug.LogWarning("[VRFileBrowser] Movie loading failed");
                }
                UpdateHelpText();
            }
            // Block all other input during loading
            if (_movieState == MovieState.Loading)
            {
                UpdateHelpText(); // refresh progress
                return;
            }
        }

        // Movie FPS adjustment — works even when browser is closed during playback
        if (_movieState == MovieState.Playing)
            HandleMovieFpsAdjust();

        // Movie stop — keyboard only. In VR the joystick is used for browsing and
        // locomotion, so movie mode is a desktop-side feature.
        if (_movieState == MovieState.Playing && !XRSettings.isDeviceActive)
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                StopMovieMode();
                return;
            }
        }

        // Other world-space panels own the input while they are visible.
        if (_optMenu == null) _optMenu = FindAnyObjectByType<VROptionsMenu>();
        if (_smbPanel == null) _smbPanel = FindAnyObjectByType<SmbSettingsPanel>();
        bool otherUiOpen = (_optMenu != null && _optMenu.IsOpen) || (_smbPanel != null && _smbPanel.IsOpen);
        if (otherUiOpen && IsOpen) return;

        if (XRSettings.isDeviceActive)
        {
            // A modal panel (options / SMB keyboard) owns the buttons while open.
            if (!otherUiOpen) HandleVRPageButtons(false);
        }
        else
        {
            HandleDesktopToggle(otherUiOpen);
        }

        if (!IsOpen) return;

        // Directory queries and file transfers run on worker threads. Keep the
        // panel responsive but do not let stale input select the wrong entry.
        if (_browseTask != null || _downloadTask != null)
        {
            UpdateTransferStatus();
            return;
        }

        RefreshCurrentFileHighlight();

        if (!XRSettings.isDeviceActive)
        {
            HandleBack();
            HandlePreloadToggle();
        }

        HandleNavigation();
        HandleSelect();
        HandleMovieButton();
    }

    /// <summary>
    /// View/Browser page buttons. Both controllers are interchangeable:
    ///   B            → from the viewer: open the browser; inside the browser:
    ///                  go up one folder, or close it when already at the top.
    ///   Trigger / A  → open the highlighted folder / load the highlighted file.
    ///   Trigger + B  → toggle preloading (browser only).
    /// </summary>
    void HandleVRPageButtons(bool otherUiOpen)
    {
        bool triggerHeld = ReadTrigger(XRNode.LeftHand) || ReadTrigger(XRNode.RightHand);
        bool bHeld = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton)
                  || ReadButton(XRNode.RightHand, CommonUsages.secondaryButton);

        // 1. Trigger + B together toggles preloading, and swallows both buttons.
        bool combo = triggerHeld && bHeld;
        if (combo && _preloadToggleReady)
        {
            _preloadToggleReady = false;
            _backReady = false;
            _trigReady = false;
            _comboActive = true;
            TogglePreload();
        }
        else if (!triggerHeld)
        {
            _preloadToggleReady = true;
            _comboActive = false;
        }

        // 2. B → open / go up / close.
        if (bHeld && _backReady && !combo)
        {
            _backReady = false;
            if (IsOpen) GoUpOrClose();
            else if (!otherUiOpen) ToggleBrowser();
        }
        else if (!bHeld)
        {
            _backReady = true;
        }
    }

    void HandleDesktopToggle(bool otherUiOpen)
    {
        if (otherUiOpen) return;
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab))
            ToggleBrowser();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ToggleBrowser()
    {
        // Block closing while movie is loading
        if (IsOpen && _movieState == MovieState.Loading)
            return;

        IsOpen = !IsOpen;
        _root.SetActive(IsOpen);
        if (IsOpen)
        {
            // Re-read smb_shares.json so edits take effect without restarting.
            SmbSourceConfig.Reload();
            PositionInFront();
            Navigate(GetPreferredOpenPath());
        }

        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = IsOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = IsOpen;
        }
    }

    string GetPreferredOpenPath()
    {
        string currentFilePath = loader != null ? loader.CurrentFilePath : null;
        if (!string.IsNullOrWhiteSpace(currentFilePath))
        {
            string currentFileDir = Path.GetDirectoryName(currentFilePath);
            if (!string.IsNullOrWhiteSpace(currentFileDir) && QuestSplatStorage.IsInsideRoot(currentFileDir))
                return currentFileDir;
        }

        return _currentPath;
    }

    // ── Input Handling ────────────────────────────────────────────────────────

    void HandleNavigation()
    {
        if (_entries.Count == 0) return;

        // Left or right stick Y (VR) or arrow keys (desktop)
        float ry = 0f;
        if (XRSettings.isDeviceActive)
            ry = ReadNavigationY();
        else
        {
            if (Input.GetKey(KeyCode.UpArrow))   ry =  1f;
            if (Input.GetKey(KeyCode.DownArrow)) ry = -1f;
        }

        _stickCD -= Time.deltaTime;
        if (Mathf.Abs(ry) > 0.5f && _stickCD <= 0f)
        {
            _sel += (ry < 0f) ? 1 : -1;
            _sel = Mathf.Clamp(_sel, 0, _entries.Count - 1);
            EnsureVisible();
            UpdateRows();
            RefreshBrowserPreload();
            _stickCD = 0.18f;
        }
        else if (Mathf.Abs(ry) <= 0.3f)
        {
            _stickCD = 0f;
        }
    }

    void HandleSelect()
    {
        bool trig = false;
        if (XRSettings.isDeviceActive)
        {
            if (_comboActive) return; // trigger was part of "Trigger + B"

            trig = ReadTrigger(XRNode.LeftHand)
                || ReadTrigger(XRNode.RightHand)
                || ReadButton(XRNode.LeftHand, CommonUsages.primaryButton)
                || ReadButton(XRNode.RightHand, CommonUsages.primaryButton);
        }
        else
        {
            trig = Input.GetKeyDown(KeyCode.Return);
        }

        if (trig && _trigReady)
        {
            _trigReady = false;
            SelectCurrent();
        }
        else if (!trig)
        {
            _trigReady = true;
        }
    }

    void TogglePreload()
    {
        if (cycler == null) return;

        cycler.preloadUpcomingFiles = !cycler.preloadUpcomingFiles;
        cycler.ApplyPreloadBudget();
        if (cycler.preloadUpcomingFiles)
            cycler.RefreshPreloadWindow();
        else if (cycler.loader != null)
            cycler.loader.SetPreloadTargets(Array.Empty<string>());

        Debug.Log($"[VRFileBrowser] Preloading {(cycler.preloadUpcomingFiles ? "ON" : "OFF")}");
        UpdateHelpText();
    }

    void HandleBack()
    {
        // Desktop keeps Backspace for "parent folder"; B is handled by
        // HandleVRPageButtons so both controllers behave identically.
        if (XRSettings.isDeviceActive) return;

        if (Input.GetKeyDown(KeyCode.Backspace))
            GoUpOrClose();
    }

    void HandlePreloadToggle()
    {
        if (XRSettings.isDeviceActive) return; // Trigger + B on the headset

        if (Input.GetKeyDown(KeyCode.P))
            TogglePreload();
    }

    void HandleMovieButton()
    {
        // Movie mode is keyboard-only: in VR the joystick click is the
        // yaw-recenter gesture and the stick itself browses / cycles files.
        if (XRSettings.isDeviceActive) return;

        if (Input.GetKeyDown(KeyCode.M) && _movieState == MovieState.Idle)
            StartMovieMode();
    }

    void HandleMovieFpsAdjust()
    {
        if (cycler == null) return;
        if (XRSettings.isDeviceActive) return; // keyboard-only, the stick drives navigation

        float rx = 0f;
        if (Input.GetKey(KeyCode.RightArrow)) rx = 1f;
        if (Input.GetKey(KeyCode.LeftArrow))  rx = -1f;

        _fpsAdjustCD -= Time.deltaTime;
        if (Mathf.Abs(rx) > 0.5f && _fpsAdjustCD <= 0f)
        {
            cycler.AdjustMovieFps(rx > 0 ? 1 : -1);
            _fpsAdjustCD = 0.15f;
        }
        else if (Mathf.Abs(rx) <= 0.3f)
        {
            _fpsAdjustCD = 0f;
        }
    }

    void StartMovieMode()
    {
        // Sync the cycler to the browser's current folder so we always load
        // from the directory the user is actually looking at, not the last
        // folder a file was selected from.
        if (cycler != null && !string.IsNullOrEmpty(_currentPath))
        {
            cycler.splatFolder = _currentPath;
            cycler.ScanFolder();
        }

        if (cycler == null || cycler.Files.Count == 0)
        {
            Debug.LogWarning("[VRFileBrowser] No files loaded to start movie mode");
            return;
        }

        var (fits, estMB, availMB) = cycler.CheckMovieFit();
        if (!fits)
        {
            Debug.LogError($"[VRFileBrowser] Movie mode: not enough RAM! Need ~{estMB}MB, available ~{availMB}MB");
            _pathText.text = $"Not enough RAM! Need ~{estMB}MB, have ~{availMB}MB";
            return;
        }

        _movieLoadedCount = 0;
        _movieTotalCount = cycler.Files.Count;
        _movieState = MovieState.Loading;

        bool started = cycler.BeginMovieLoad((loaded, total) =>
        {
            _movieLoadedCount = loaded;
            _movieTotalCount = total;
        });

        if (!started)
        {
            _movieState = MovieState.Idle;
            Debug.LogError("[VRFileBrowser] Failed to start movie loading");
            RefreshBrowserPreload();
        }
        else
        {
            Debug.Log($"[VRFileBrowser] Movie loading started: {_movieTotalCount} frames (~{estMB}MB)");
        }

        UpdateHelpText();
    }

    void StopMovieMode()
    {
        if (cycler != null)
            cycler.StopMovie();
        _movieState = MovieState.Idle;
        UpdateHelpText();
    }

    // ── File System ───────────────────────────────────────────────────────────

    void Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !QuestSplatStorage.IsInsideRoot(path))
            path = _rootPath;

        _currentPath = path;
        _lastBrowseError = null;
        _browseStatus = null;
        _browseTask = null;
        _browseGeneration++;
        _entries.Clear();
        _sel = 0;
        _scroll = 0;

        // Parent entry, but never allow navigation above the configured browse root.
        string parentPath = GetParentPath(_currentPath);
        if (!PathsEqual(_currentPath, _rootPath) && !string.IsNullOrEmpty(parentPath)
            && QuestSplatStorage.IsInsideRoot(parentPath))
            _entries.Add(new Entry { name = "..", path = parentPath, isDir = true });

        if (SplatFileSystem.IsRemotePath(path))
        {
            // A directory query on a network share can take seconds; it must
            // never block the VR render thread.
            string requestedPath = path;
            _browseTaskGeneration = _browseGeneration;
            _browseStatus = "opening network folder...";
            _browseTask = Task.Run(delegate { return SplatFileSystem.ListEntries(requestedPath, true); });
            UpdatePath();
            UpdateRows();
            return;
        }

        try
        {
            ApplyListing(SplatFileSystem.ListEntries(path, false));
        }
        catch (UnauthorizedAccessException)
        {
            _lastBrowseError = "Access denied by Android storage restrictions.";
            FinishFailedListing();
        }
        catch (Exception ex)
        {
            _lastBrowseError = ex.Message;
            Debug.LogWarning($"[VRFileBrowser] Cannot list {_currentPath}: {ex.Message}");
            FinishFailedListing();
        }
    }

    void ApplyListing(List<SplatEntry> listing)
    {
        foreach (var item in listing)
        {
            _entries.Add(new Entry
            {
                name = item.name,
                path = item.localPath,
                isDir = item.isDir,
                isSmbSetup = item.isSmbSetup
            });
        }

        // The browser already paid for the network listing; hand it to the
        // cycler so R / F and movie mode work in the same SMB folder.
        if (SplatFileSystem.IsRemotePath(_currentPath) && cycler != null)
        {
            var files = listing.Where(e => !e.isDir && !e.isSmbSetup).Select(e => e.localPath).ToList();
            cycler.SetFolderEntries(_currentPath, files);
        }

        UpdateLibraryHint();
        FocusCurrentFileEntry();
        UpdatePath();
        UpdateRows();
        RefreshBrowserPreload();
        UpdateHelpText();
    }

    /// <summary>Explains an empty folder so a black scene is not mistaken for a crash.</summary>
    void UpdateLibraryHint()
    {
        _libraryHint = null;

        if (QuestSplatStorage.IsDownloadFolder(_currentPath))
        {
            if (_entries.Any(e => !e.isDir)) return;
            _libraryHint = "No splat files in Download yet.\n" +
                "Copy .ply/.spz/.spx/.sog into Download\n" +
                "then press B to return to Splats/.";
            return;
        }

        if (!PathsEqual(_currentPath, _rootPath)) return;
        if (SmbSourceConfig.EnabledCount > 0) return;
        if (_entries.Any(e => !e.isDir)) return;
        _libraryHint = "No splat files yet.\n" +
            "Copy .ply/.spz/.spx/.sog into Splats/\n" +
            "or set enabled=true in smb_shares.json.";
    }

    /// <summary>True when the folder can be enumerated (permission already granted).</summary>
    static bool CanListFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using (var walker = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
            {
                walker.MoveNext();
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    void FinishFailedListing()
    {
        UpdatePath();
        UpdateRows();
        RefreshBrowserPreload();
        UpdateHelpText();
    }

    void PumpBrowse()
    {
        if (_browseTask == null || !_browseTask.IsCompleted) return;

        var task = _browseTask;
        int generation = _browseTaskGeneration;
        _browseTask = null;
        _browseStatus = null;
        if (generation != _browseGeneration)
        {
            UpdatePath();
            return; // a newer navigation superseded this result
        }

        List<SplatEntry> listing = null;
        try
        {
            listing = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _lastBrowseError = ex.Message;
            Debug.LogWarning($"[VRFileBrowser] SMB listing failed for {_currentPath}: {ex.Message}");
        }

        if (listing != null) ApplyListing(listing);
        else FinishFailedListing();
    }

    void PumpDownload()
    {
        if (_downloadTask == null || !_downloadTask.IsCompleted) return;

        var task = _downloadTask;
        Entry entry = _downloadEntry;
        _downloadTask = null;
        _downloadStatus = null;
        SplatFileSystem.ForgetDownload(entry.path);

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _lastBrowseError = "SMB download failed: " + ex.Message;
            Debug.LogWarning($"[VRFileBrowser] SMB download failed for {entry.path}: {ex.Message}");
            UpdatePath();
            UpdateHelpText();
            return;
        }

        FinishSelect(entry);
    }

    void UpdateTransferStatus()
    {
        if (!IsOpen) return;

        if (_downloadTask != null)
        {
            var progress = SplatFileSystem.GetTransferProgress(_downloadEntry.path);
            long doneMb = progress.bytesDone / (1024 * 1024);
            long totalMb = progress.bytesTotal / (1024 * 1024);
            string percent = progress.bytesTotal > 0
                ? (100.0 * progress.bytesDone / progress.bytesTotal).ToString("F0") + "%"
                : "...";
            _downloadStatus = $"SMB: {_downloadEntry.name}  {doneMb}/{totalMb} MB  ({percent})";
        }

        UpdatePath();
    }

    static string GetParentPath(string path)
    {
        // The first level below SmbCache is one configured share; going up from
        // there returns to the local Splats root.
        if (SplatFileSystem.IsRemotePath(path) && SplatFileSystem.IsSourceRoot(path))
            return SplatFileSystem.LocalBrowseRoot;

        // Public Downloads sits outside the library, so "up" leads back to it
        // instead of to /sdcard, which the app may not list.
        if (QuestSplatStorage.IsDownloadFolder(path))
            return SplatFileSystem.LocalBrowseRoot;

        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(path));
            return parent != null ? parent.FullName : null;
        }
        catch (Exception) { return null; }
    }

    void FocusCurrentFileEntry()
    {
        string currentFilePath = loader != null ? loader.CurrentFilePath : null;
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].isDir || !PathsEqual(_entries[i].path, currentFilePath))
                continue;

            _sel = i;
            CenterSelection();
            return;
        }
    }

    void CenterSelection()
    {
        int maxScroll = Mathf.Max(0, _entries.Count - ROWS);
        _scroll = Mathf.Clamp(_sel - ROWS / 2, 0, maxScroll);
    }

    void SelectCurrent()
    {
        if (_browseTask != null || _downloadTask != null) return;
        if (_sel < 0 || _sel >= _entries.Count) return;
        var entry = _entries[_sel];

        if (entry.isSmbSetup)
        {
            OpenSmbSettings();
            return;
        }

        if (entry.isDir)
        {
            Navigate(entry.path);
            return;
        }

        if (loader == null) return;

        // SMB files are mirrored into the cache before the existing readers
        // (which need seekable local files) can decode them.
        if (SplatFileSystem.NeedsDownload(entry.path))
        {
            _downloadEntry = entry;
            _downloadStatus = "SMB: downloading " + entry.name + "...";
            _downloadTask = SplatFileSystem.EnsureLocalCachedAsync(entry.path);
            UpdatePath();
            return;
        }

        FinishSelect(entry);
    }

    void FinishSelect(Entry entry)
    {
        if (loader == null) return;

        // Stop any active movie playback/loading before loading a new file
        if (_movieState != MovieState.Idle)
            StopMovieMode();

        if (!loader.LoadFile(entry.path))
        {
            _lastBrowseError = "Could not load " + entry.name;
            UpdatePath();
            UpdateHelpText();
            return;
        }

        // Sync SplatCycler to the loaded file so B/A cycling continues from here
        if (cycler != null && !string.IsNullOrEmpty(_currentPath))
        {
            if (SplatFileSystem.IsRemotePath(_currentPath))
            {
                var files = _entries.Where(e => !e.isDir && !e.isSmbSetup).Select(e => e.path).ToList();
                cycler.SetFolderEntries(_currentPath, files);
            }
            else
            {
                cycler.splatFolder = _currentPath;
                cycler.ScanFolder();
            }

            string fileName = Path.GetFileName(entry.path);
            for (int i = 0; i < cycler.Files.Count; i++)
            {
                if (Path.GetFileName(cycler.Files[i]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    cycler.SetIndex(i);
                    break;
                }
            }
        }

        // The camera is deliberately not moved here: the splat transform is what
        // the user manipulates, and its position / rotation carries over to the
        // file that was just loaded.

        // The user may have closed the panel while the SMB download was running.
        if (IsOpen) ToggleBrowser(); // close after loading
    }

    void OpenSmbSettings()
    {
        var panel = FindAnyObjectByType<SmbSettingsPanel>();
        if (panel == null)
        {
            var go = new GameObject("SmbSettingsPanel");
            DontDestroyOnLoad(go);
            panel = go.AddComponent<SmbSettingsPanel>();
        }

        if (IsOpen) ToggleBrowser();
        if (!panel.IsOpen) panel.Toggle();
    }

    void GoUp()
    {
        if (_browseTask != null || _downloadTask != null) return;
        if (string.IsNullOrEmpty(_currentPath)) return;
        string parent = GetParentPath(_currentPath);
        if (string.IsNullOrEmpty(parent)) return;
        Navigate(parent);
    }

    /// <summary>
    /// B button behaviour: step up one folder, and close the browser when there
    /// is no folder above the current one (so B always has a visible effect).
    /// </summary>
    void GoUpOrClose()
    {
        if (_browseTask != null || _downloadTask != null)
            return;

        string parent = string.IsNullOrEmpty(_currentPath) ? null : GetParentPath(_currentPath);
        bool hasParent = !string.IsNullOrEmpty(parent)
            && !PathsEqual(parent, _currentPath)
            && QuestSplatStorage.IsInsideRoot(parent);

        if (hasParent)
        {
            Navigate(parent);
            return;
        }

        ToggleBrowser();
    }

    // ── UI Building ───────────────────────────────────────────────────────────

    void BuildUI()
    {
        // World-space Canvas
        _root = new GameObject("VRFileBrowser");
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _root.AddComponent<CanvasScaler>();

        var canvasRT = _root.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(CW, CH);
        _root.transform.localScale = Vector3.one * SCALE;

        // Background
        var bg = MakeChild(_root.transform, "BG");
        bg.AddComponent<Image>().color = COL_BG;
        Stretch(bg);

        // ── Layout from top down ──
        float y = -PAD;

        // Path bar
        _pathText = MakeText(bg.transform, "Path", "", FONT_PATH, COL_PATH,
            PAD + 4, y, CW - PAD * 2 - 8, PATH_H);
        y -= PATH_H;

        // Separator
        var sep = MakeChild(bg.transform, "Sep");
        sep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        SetRect(sep, PAD, y, CW - PAD * 2, 1);
        y -= 4;

        // Entry rows
        _rowBgs  = new Image[ROWS];
        _rowTexts = new Text[ROWS];
        for (int i = 0; i < ROWS; i++)
        {
            var rowBg = MakeChild(bg.transform, $"RowBg{i}");
            _rowBgs[i] = rowBg.AddComponent<Image>();
            _rowBgs[i].color = COL_CLEAR;
            SetRect(rowBg, PAD, y, CW - PAD * 2, ROW_H);

            _rowTexts[i] = MakeText(bg.transform, $"Row{i}", "", FONT_ENTRY, COL_FILE,
                PAD + 12, y, CW - PAD * 2 - 24, ROW_H);
            y -= ROW_H;
        }

        // Hint bar at bottom
        y -= 4;
        string vr   = "[L/R Stick] Navigate    [L/R Trigger or A] Select    [B] Back    [Y] Close";
        string desk  = "[Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close";
        _hintText = MakeText(bg.transform, "Hint", XRSettings.isDeviceActive ? vr : desk,
            FONT_HINT, COL_HINT, PAD, y, CW - PAD * 2, HINT_H, TextAnchor.MiddleCenter);

        var helpPanel = MakeChild(_root.transform, "HelpPanel");
        var helpBg = helpPanel.AddComponent<Image>();
        helpBg.color = new Color(0.05f, 0.05f, 0.07f, 0.92f);
        SetRect(helpPanel, CW + 24, -PAD, HelpPanelWidth, HelpPanelHeight);

        _helpText = MakeText(helpPanel.transform, "Help", "", FONT_HINT, Color.white,
            16, -16, HelpPanelWidth - 32, HelpPanelHeight - 32, TextAnchor.UpperLeft);
        _helpText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _helpText.verticalOverflow = VerticalWrapMode.Overflow;
        UpdateHelpText();
    }

    // ── UI Helpers ────────────────────────────────────────────────────────────

    static GameObject MakeChild(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void SetRect(GameObject go, float x, float y, float w, float h)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot     = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeText(Transform parent, string name, string text,
        int fontSize, Color color, float x, float y, float w, float h,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var go = MakeChild(parent, name);
        SetRect(go, x, y, w, h);
        var t = go.AddComponent<Text>();
        t.text      = text;
        t.fontSize  = fontSize;
        t.color     = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow   = VerticalWrapMode.Truncate;
        if (_font != null) t.font = _font;
        return t;
    }

    // ── UI Update ─────────────────────────────────────────────────────────────

    void UpdatePath()
    {
        string text = TruncatePath(_currentPath ?? string.Empty, 70);
        if (SplatFileSystem.IsRemotePath(_currentPath))
            text = "[SMB] " + text;

        if (_browseTask != null)
            text = "SMB: " + (_browseStatus ?? "working...") + "  |  " + text;
        else if (_downloadTask != null)
            text = (_downloadStatus ?? "SMB: downloading...") + "  |  " + text;
        else if (_lastBrowseError != null)
            text = "Error: " + _lastBrowseError + "  |  " + text;

        _pathText.text = text;
    }

    void UpdateRows()
    {
        string currentFilePath = loader != null ? loader.CurrentFilePath : null;

        for (int i = 0; i < ROWS; i++)
        {
            int idx = _scroll + i;
            if (idx < _entries.Count)
            {
                var e = _entries[idx];
                bool isCurrentFile = !e.isDir && PathsEqual(e.path, currentFilePath);
                bool isParentEntry = e.isDir && e.name == "..";
                string prefix = isParentEntry ? "< "
                    : (e.isDir ? "\u25B6 "
                    : (e.isSmbSetup ? "* " : "   "));
                string label = e.name;
                if (isParentEntry && QuestSplatStorage.IsDownloadFolder(_currentPath))
                    label = "Splats library (with SMB shares)";
                _rowTexts[i].text  = prefix + label;
                _rowTexts[i].color = e.isSmbSetup ? COL_SETUP : (e.isDir ? COL_DIR : COL_FILE);

                if (idx == _sel)
                    _rowBgs[i].color = COL_SEL;
                else if (isCurrentFile)
                    _rowBgs[i].color = COL_CURRENT;
                else if (i % 2 == 1)
                    _rowBgs[i].color = COL_ROW_ALT;
                else
                    _rowBgs[i].color = COL_CLEAR;
            }
            else
            {
                _rowTexts[i].text  = "";
                _rowBgs[i].color   = COL_CLEAR;
            }
        }

        // Update hint with item count
        int dirs  = _entries.Count(e => e.isDir) - (_entries.Count > 0 && _entries[0].isDir && _entries[0].name == ".." ? 1 : 0);
        int files = _entries.Count(e => !e.isDir);
        string countInfo = $"{dirs} folder(s), {files} file(s)";
        if (_entries.Count > 0)
            countInfo += $"   [{_sel + 1}/{_entries.Count}]";
        countInfo += $"   |   FPS: {_displayFps:F1}";

        string controls = XRSettings.isDeviceActive
            ? "[Stick] Navigate    [Trigger / A] Select    [B] Back    [Trigger+B] Preload"
            : "[Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close";

        string movieInfo = "";
        if (_movieState == MovieState.Playing && cycler != null)
            movieInfo = $"   |   Movie: {cycler.movieFps} FPS";
        else if (_movieState == MovieState.Loading)
        {
            float pct = _movieTotalCount > 0 ? (float)_movieLoadedCount / _movieTotalCount * 100f : 0f;
            movieInfo = $"   |   Movie: Loading {_movieLoadedCount}/{_movieTotalCount} ({pct:F0}%)";
        }

        _hintText.text = $"{countInfo}\n{controls}{movieInfo}";
        UpdateHelpText();
    }

    void UpdateHelpText()
    {
        if (_helpText == null) return;

        bool preloadOn = cycler != null && cycler.preloadUpcomingFiles;
        string preloadStatus = preloadOn
            ? $"Preload: ON (current +{BrowserPreloadAheadCount} ahead, {cycler.preloadRamFraction:P0} RAM)"
            : "Preload: OFF";

        // Movie status line
        string movieStatus;
        if (_movieState == MovieState.Loading)
        {
            float pct = _movieTotalCount > 0 ? (float)_movieLoadedCount / _movieTotalCount * 100f : 0f;
            movieStatus = $"Movie: LOADING {_movieLoadedCount}/{_movieTotalCount} ({pct:F0}%)";
        }
        else if (_movieState == MovieState.Playing)
        {
            int fps = cycler != null ? cycler.movieFps : 0;
            int frames = cycler != null && cycler.loader != null ? cycler.loader.MovieFrameCount : 0;
            movieStatus = $"Movie: PLAYING {fps} FPS ({frames} frames)";
        }
        else
        {
            movieStatus = "Movie: OFF";
        }

        _helpText.text = XRSettings.isDeviceActive
            ? "Browser page (both hands identical)\n"
            + "B: back to viewer / parent folder\n"
            + "Trigger or A: open folder / load\n"
            + "Trigger + B: toggle preload\n"
            + "Stick up/down: browse list\n\n"
            + "Viewer page (moves the splat, never the camera)\n"
            + "B: open browser\n"
            + "Stick up/down: zoom (closer / further)\n"
            + "Stick left/right: prev / next file\n"
            + "Trigger + Stick: move splat (Y = up/down)\n"
            + "Grip + swing controller: orbit splat\n"
            + "Stick click: reset rotation\n"
            + "Stick press+hold: reset position\n"
            + "Both grips: options\n\n"
            + (_libraryHint == null ? "" : _libraryHint + "\n\n")
            + "File Sources\n"
            + "Download: default folder\n"
            + "Splats/: local library\n"
            + "SMB: Splats/smb_shares.json\n"
            + $"Shares enabled: {SmbSourceConfig.EnabledCount}\n"
            + "SMB files cached to SmbCache/\n\n"
            + (_lastBrowseError == null ? "" : $"Status: {_lastBrowseError}\n\n")
            + $"FPS: {_displayFps:F1}\n"
            + preloadStatus + "\n"
            + movieStatus
            : "Browser\n"
            + "Esc / Tab: open / close\n"
            + "Up / Down: browse list\n"
            + "Enter: open / load\n"
            + "Backspace: parent folder\n"
            + "P: toggle preload\n"
            + "M: start / stop movie\n\n"
            + "Movie Playback\n"
            + "Left / Right: FPS -/+\n"
            + "M: stop movie\n\n"
            + (_libraryHint == null ? "" : _libraryHint + "\n\n")
            + "File Sources\n"
            + "Download: default folder\n"
            + "Splats/: local library\n"
            + "SMB: Splats/smb_shares.json\n"
            + $"Shares enabled: {SmbSourceConfig.EnabledCount}\n"
            + "SMB files cached to SmbCache/\n\n"
            + (_lastBrowseError == null ? "" : $"Status: {_lastBrowseError}\n\n")
            + "Scene\n"
            + "Mouse: look    WASD: move\n"
            + "Space / C: up / down\n"
            + "R / F: next / previous splat\n"
            + "Q / E: rotate splat\n"
            + "Home: reset    End: flip\n\n"
            + "Options: O key\n\n"
            + $"FPS: {_displayFps:F1}\n"
            + preloadStatus + "\n"
            + movieStatus;
    }

    void UpdateFpsCounter()
    {
        if (Time.unscaledDeltaTime <= 0f)
            return;

        _fpsAccumTime += Time.unscaledDeltaTime;
        _fpsAccumFrames++;

        if (_fpsAccumTime < FpsUpdateInterval)
            return;

        _displayFps = _fpsAccumFrames / _fpsAccumTime;
        _fpsAccumTime = 0f;
        _fpsAccumFrames = 0;

        if (IsOpen)
            UpdateRows();
    }

    void RefreshCurrentFileHighlight()
    {
        string currentFilePath = loader != null ? loader.CurrentFilePath : null;
        if (PathsEqual(currentFilePath, _lastHighlightedFilePath))
            return;

        _lastHighlightedFilePath = currentFilePath;
        UpdateRows();
    }

    void RefreshBrowserPreload()
    {
        if (!IsOpen || loader == null || cycler == null)
            return;

        string anchorPath = GetBrowserPreloadAnchorPath();
        bool preloadEnabled = cycler.preloadUpcomingFiles && _movieState == MovieState.Idle;
        if (preloadEnabled
            && PathsEqual(_currentPath, _lastPreloadFolderPath)
            && PathsEqual(anchorPath, _lastPreloadAnchorPath)
            && preloadEnabled == _lastPreloadEnabled)
            return;

        _lastPreloadFolderPath = _currentPath;
        _lastPreloadAnchorPath = anchorPath;
        _lastPreloadEnabled = preloadEnabled;

        if (!preloadEnabled)
        {
            loader.SetPreloadTargets(Array.Empty<string>());
            return;
        }

        var filesInView = _entries
            .Where(entry => !entry.isDir && !entry.isSmbSetup)
            .Select(entry => entry.path)
            .ToList();
        if (filesInView.Count == 0)
        {
            loader.SetPreloadTargets(Array.Empty<string>());
            return;
        }

        anchorPath ??= filesInView[0];

        int anchorIndex = filesInView.FindIndex(path => PathsEqual(path, anchorPath));
        if (anchorIndex < 0)
            anchorIndex = 0;

        long budget = loader.PreloadBudgetBytes;
        long accumulated = 0;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int offset = 0; offset <= BrowserPreloadAheadCount; offset++)
        {
            int forward = anchorIndex + offset;
            if (forward < filesInView.Count)
                TryAddPreloadTarget(filesInView[forward], desired, ref accumulated, budget);
        }

        loader.SetPreloadTargets(desired);
        foreach (string filePath in desired)
        {
            if (PathsEqual(filePath, loader.CurrentFilePath))
                continue;
            loader.BeginPreload(filePath);
        }
    }

    string GetBrowserPreloadAnchorPath()
    {
        if (_sel >= 0 && _sel < _entries.Count && !_entries[_sel].isDir)
            return _entries[_sel].path;

        if (loader != null && !string.IsNullOrEmpty(loader.CurrentFilePath))
            return loader.CurrentFilePath;

        return null;
    }

    static void TryAddPreloadTarget(string filePath, HashSet<string> desired, ref long accumulated, long budget)
    {
        if (string.IsNullOrWhiteSpace(filePath) || desired.Contains(filePath))
            return;

        long cost = RuntimeSplatLoader.EstimateAssetBytes(filePath);
        if (budget > 0 && accumulated + cost > budget)
            return;

        desired.Add(filePath);
        accumulated += cost;
    }

    static bool PathsEqual(string lhs, string rhs)
    {
        return string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase);
    }

    void EnsureVisible()
    {
        if (_sel < _scroll)           _scroll = _sel;
        if (_sel >= _scroll + ROWS)   _scroll = _sel - ROWS + 1;
    }

    static string TruncatePath(string p, int maxLen)
    {
        if (p.Length <= maxLen) return p;
        return "..." + p.Substring(p.Length - maxLen + 3);
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 pos = cam.transform.position + fwd * spawnDistance;
        _root.transform.position = pos;
        // Canvas front faces toward the user
        _root.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── XR Input Helpers ──────────────────────────────────────────────────────

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static float ReadNavigationY()
    {
        float leftY = ReadStickY(XRNode.LeftHand);
        float rightY = ReadStickY(XRNode.RightHand);
        return Mathf.Abs(leftY) >= Mathf.Abs(rightY) ? leftY : rightY;
    }

    static float ReadStickY(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.y;
        return 0f;
    }

    static bool ReadTrigger(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.trigger, out float v))
            return v > 0.5f;
        return false;
    }
}
