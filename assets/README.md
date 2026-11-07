# LiveStreamGateway 应用图标

图标采用 Web 面板的深蓝、蓝色及青色，使用播放三角与两路汇入的网关轮廓表达直播流中继。图形之外保留透明背景，不包含文字。

- `LiveStreamGateway.png`：图像生成工具内置模式生成的原始设计。
- `LiveStreamGateway.ico`：同一设计的 Windows 多尺寸图标，包含 16、20、24、32、40、48、64、128、256 px，均保留透明度。
- EXE 使用 `ApplicationIcon`；托盘加载 `LiveStreamGateway.AppIcon.ico` 内嵌资源，无需单独分发图标文件。
- Web 标签页直接使用同一 ICO；登录页及面板页眉使用其中原有的 256 px PNG 帧，Windows 与 Docker 均无需额外分发图标文件。

在 Windows PowerShell / PowerShell 中重新打包图标（不需要额外依赖）：

```powershell
./tools/Build-AppIcon.ps1
```

## 最终生成提示词

```text
Use case: logo-brand
Asset type: final Windows desktop EXE and notification-tray application icon for LiveStreamGateway, a self-hosted live-stream relay and IPTV gateway; not a mobile app mockup.
Primary request: create ONE original, polished, extremely simple icon combining media playback and stream routing. A bold white right-facing play triangle sits inside a thick blue/cyan rounded gateway outline, with two short thick stream strokes feeding into the gateway from the left, forming one compact recognizable emblem. Use few large shapes and balanced negative space, no tiny details. The silhouette must remain legible when reduced to 16x16 pixels.
Scene/backdrop: a deep navy rounded-square app tile (#0b0f19), surrounded by a genuinely transparent margin; the tile occupies about 92% of the square canvas. The emblem occupies about 70% of the tile. Center it with optical balance.
Style/medium: precise flat vector-like raster artwork with clean antialiased edges, contemporary Windows utility identity; front-facing, no perspective, restrained polish. Colors follow the existing Web UI: vivid blue #3b82f6, cyan #06b6d4, white #f1f5f9, navy #0b0f19.
Constraints: one icon only, square composition; absolutely no letters, text, numbers, watermark, labels, mockup, wallpaper, checkerboard, drop shadow, scene or decorative glow; do not make a contact sheet. Preserve genuine transparency outside the rounded-square tile.
```
