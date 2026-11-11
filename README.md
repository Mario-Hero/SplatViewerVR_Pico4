# SplatViewer VR Android （Pico4 optimized）

版本 v0.1 · 2026-11-11

一个跑在 Pico 4（Android + OpenXR）上的高斯泼溅（3D Gaussian Splatting）查看器。
A 3D Gaussian Splatting viewer running on Pico 4 (Android + OpenXR).

针对 pico4 普通版优化，但是目前还是比较卡，预览100万点的高斯仅为15FPS。

Optimized for the regular version of Pico 4, but it still lags quite a bit, with a preview of 1 million points generating only 15 FPS.

可以直接从头显的 Download 目录、内置库或 SMB 共享里打开 `.ply` / `.spz` / `.sog` / `.spx` 文件。
It opens `.ply` / `.spz` / `.sog` / `.spx` files straight from the headset's Download folder, the built-in library, or an SMB share.

支持ply、spz、sog格式。

Supports ply, spz, and sog formats.

SMB功能正在开发中。 The SMB function is currently under development.

> **本项目基于 [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android) 改造而来。**
> **This project is derived from [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android)**.

## 1. 快速上手 / Quick start

1. 把 `.ply` / `.spz` / `.sog` / `.spx` 复制到头显的 `Download` 文件夹。
   Copy your `.ply` / `.spz` / `.sog` / `.spx` files to `Download` folder on the headset.
2. 戴头显启动应用，文件浏览器会自动打开，并停在 Download 目录。
   Put on the headset and launch — the file browser opens automatically in Download.
3. 扳机或 A 键进入文件夹 / 打开文件。进入后按B键重新打开文件浏览器。
   Trigger or A to enter a folder / open a file。Then press **B** to return to the file browser; 

## 2. 按键说明 / Controls

左右手柄功能完全对称，两只手的按键可以互换、也可以混用。
Both controllers are fully interchangeable — every gesture works on either hand, mixing them is fine.

### 2.1 文件浏览器页面 / File browser page

- **摇杆 ↑↓**：上下浏览列表
  **Stick up / down**: scroll the list
- **扳机或 A**：进入文件夹 / 打开文件
  **Trigger or A**: enter a folder / open a file
- **B**：返回上一级；已在最顶层时再按 B 关闭浏览器，回到播放器
  **B**: go up one folder; pressing B at the top level closes the browser and returns to the viewer
- **扳机 + B 同时按**：开启 / 关闭预加载（默认关闭）
  **Trigger + B together**: toggle preloading (off by default)

### 2.2 播放器页面 / Viewer page

- **摇杆 ↑↓**：缩放。点云沿“眼睛 → 点云中心”这条线靠近或远离，看起来就是整体放大 / 缩小
  **Stick up / down**: zoom. The splat slides along the eye→centre line, so it visually grows or shrinks
- **摇杆 ←→**：切换上一个 / 下一个文件
  **Stick left / right**: previous / next file in the folder
- **扳机 + 摇杆 ↑↓**：点云整体上移 / 下移
  **Trigger + stick up / down**: lift or lower the splat
- **扳机 + 摇杆 ←→**：点云左右平移
  **Trigger + stick left / right**: slide the splat sideways
- **按住 Grip 并挥动手柄**：抓取旋转。手柄转多少度，点云就转多少度，整个场景绕着头显转动
  **Hold Grip and swing the controller**: grab-orbit. The splat follows your wrist 1:1 and turns around your head
- **摇杆单击**：点云旋转复位
  **Stick click (short press)**: reset the splat rotation
- **摇杆长按约 0.6 秒**：点云位置复位
  **Stick press and hold ~0.6 s**: reset the splat position
- **B**：打开文件浏览器；
  **B**: open the file browser; 
- **双手同时按 Grip**：打开 / 关闭选项菜单
  **Both grips at once**: open / close the options menu

### 2.3 选项菜单 / Options menu

双手同时按 Grip 打开。摇杆 ↑↓ 选择条目，摇杆 ←→ 调整数值。
Opened with both grips. Stick up/down selects, stick left/right adjusts.

| 条目 / Item | 范围 / Range | 说明 / Notes |
|---|---|---|
| Render Scale | 0.60 – 1.40 | 眼缓冲渲染比例，1.0 = 原生 / eye buffer scale, 1.0 = native |
| SH Order | 0 – 3 | 球谐阶数，0 最省性能 / SH order, 0 is cheapest |
| Sort Every N Frames | 1 – 30 | 每 N 帧重排一次 / re-sort every N frames |
| Splat Budget (k) | 0 – 2000 | 0 = 不抽稀，保留全部点 / 0 = keep every splat |

### 2.4 桌面端（编辑器调试）/ Desktop (editor only)

WASD / 空格 / C 移动点云、滚轮缩放、R / F 切文件、X / Home 复位、M 切换序列播放、Q / E 旋转。
WASD / Space / C move the splat, wheel zooms, R / F cycle files, X / Home reset, M toggles movie playback, Q / E rotate.

## 3. 依赖 / Dependency

**Unity 6000.3** 

## 4. 已知限制 / Known limitations

- 单个 `GraphicsBuffer` 上限 128 MB，超大数据会自动降级球谐编码，画质会略降。
  A single `GraphicsBuffer` is capped at 128 MB; larger assets fall back to a lower SH encoding, which costs a little quality.
- 固定注视点渲染（FFR，Medium）始终开启，周边视野会比中心略糊。
  Fixed foveated rendering (FFR, Medium) is always on, so peripheral vision is slightly softer than the centre.
- 序列播放（Movie）目前只支持桌面端，键盘 M 键切换。
  Movie playback is desktop-only for now (M key).

## 5. 代码结构 / Source layout

```
Assets/Scripts/VR/
├── ViewerStartup.cs        启动流程：渲染比例、性能等级、存储权限 / startup: render scale, perf level, permissions
├── VRRig.cs                XR 相机与出生点 / XR camera and spawn pose
├── RuntimeSplatLoader.cs   运行时加载与转换（PLY/SPZ/SOG/SPX）/ runtime loader and converter
├── SplatCycler.cs          文件轮换、预加载、序列播放 / file cycling, preload, movie mode
├── VRLocomotion.cs         所有移动与旋转手势 / every move & rotate gesture
├── SplatRotator.cs         初始朝向与桌面端旋转 / start orientation and desktop rotation
├── VRFileBrowser.cs        世界空间文件浏览器与帮助面板 / world-space file browser and help panel
├── VROptionsMenu.cs        选项菜单 / options menu
├── SmbSettingsPanel.cs     SMB 配置面板与虚拟键盘 / SMB panel with virtual keyboard
├── QuestSplatStorage.cs    存储路径与权限 / storage paths and permissions
├── SplatFileSystem.cs      本地 / SMB 统一的文件视图 / unified local + SMB file view
├── SmbSourceConfig.cs      SMB 共享配置读写 / SMB share config
├── SmbShareClient.cs       SMB2/3 客户端（SMBLibrary）/ SMB2/3 client
├── PlayCanvasSogReader.cs  SOG 解码 / SOG decoder
└── SpxReader.cs            SPX 解码 / SPX decoder
```

## 8. 来源与致谢 / Upstream & credits

本项目**基于 [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android) 改造**，安卓端的运行时加载、渲染管线和交互框架都来自该工程。
This project is **derived from [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android)** — the Android runtime loader, render pipeline and interaction framework all originate there.

- 上游仓库 / Upstream repository：https://github.com/Enndee/Splatviewer_VR_Android
- 本版本相对上游的主要改动 / Main changes on top of upstream：
  - 文件浏览器默认目录改为 `/sdcard/Download`，新增 SMB2/SMB3 共享浏览与缓存。
    The browser now starts in `/sdcard/Download`, with added SMB2/SMB3 browsing and caching.
  - 所有手柄手势改为“只移动点云、不移动相机”，切换文件继承位移与旋转。
    All controller gestures now move the splat only, never the camera, and the transform carries over on file switch.
  - 按住 Grip 挥手柄 = 抓取旋转；扳机 + 摇杆 = 平移与升降；摇杆 ↑↓ = 缩放。
    Grip + wrist swing = grab-orbit; trigger + stick = pan and lift; stick up/down = zoom.
  - 运行时按点数自动选择 SH 编码，规避移动 GPU 单缓冲 128 MB 上限。
    Runtime SH encoding selection keeps assets under the mobile 128 MB single-buffer limit.
  - 选项菜单增加 Render Scale / SH Order / Sort Every N Frames / Splat Budget，并启用固定注视点渲染与性能等级请求。
    Options menu gained Render Scale / SH Order / Sort Every N Frames / Splat Budget, plus fixed foveated rendering and performance-level requests.

## 9. 许可 / License

本工程代码以 MIT 协议发布。
This project's own code is released under the MIT License.

上游工程 [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android) 的版权归其作者所有，本项目在其基础上修改，保留原有版权声明。
The upstream project [Enndee/Splatviewer_VR_Android](https://github.com/Enndee/Splatviewer_VR_Android) is copyright its authors; this fork keeps the original notices.

高斯泼溅渲染基于 Aras Pranckevičius 的 *Gaussian Splatting for Unity*（MIT，见 `Source/Packages/org.nesnausk.gaussian-splatting/LICENSE.md`）。
The Gaussian splatting renderer is based on Aras Pranckevičius' *Gaussian Splatting for Unity* (MIT, see `Source/Packages/org.nesnausk.gaussian-splatting/LICENSE.md`).

Pico OpenXR 插件版权归 Pico / 字节跳动所有，按其自身许可使用。
The Pico OpenXR plugin is copyright Pico / ByteDance and is used under its own license.

