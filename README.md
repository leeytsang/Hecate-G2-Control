# HECATE G2 Control

<p align="center">
  <img src="src/PeripheralBattery.App/Assets/app.png" width="96" alt="HECATE G2 Control icon" />
</p>

一个面向 **HECATE G2 无线版（2.4G + BLE 控制通道）** 的本地 Windows 控制台。

它解决的核心问题不是“让 2.4G 接收器出现”，而是让 Windows 把 2.4G 音频端点当作可插拔设备处理：

- 耳机真实开机并连接到 2.4G 接收器：显示 HECATE G2 音频端点。
- 耳机关机、休眠或切换到蓝牙模式：隐藏 HECATE G2 音频端点。
- 不修改默认音频设备，也不卸载 USB 设备或驱动；Windows 自己决定音频如何切换。

> 本项目是社区项目，与 EDIFIER / HECATE 官方没有关联。当前只在 HECATE G2 无线版 `35BB:A209` 上验证过。

## 功能

### 电量

- 通过耳机的 BLE GATT 控制通道读取真实电量。
- 在托盘菜单显示电量、连接状态和当前 EQ。
- 不发送低电量通知。

### 硬件 EQ

- G2 固件原生 **4 段 EQ**：100 Hz / 500 Hz / 2 kHz / 8 kHz。
- 每段范围 `-3 dB` 到 `+3 dB`，步进 `0.5 dB`。
- 内置预设：默认、三角洲、PUBG、无畏契约、APEX、CS2。
- 支持自定义 4 段曲线。
- 写入后会读回硬件值并校验。
- 耳机离线时保存目标 EQ，重新连接后自动应用。
- 托盘右键可直接切换 EQ，不注册全局热键。

### 2.4G 音频端点自动显隐

- 耳机在线时显示应用所匹配的 2.4G 音频端点。
- 耳机离线或切换到蓝牙时隐藏这些端点。
- 只处理 2.4G USB 音频接口 `USB\VID_35BB&PID_A209&MI_00` 下的 Render/Capture 端点。
- 不触碰蓝牙音频端点，不通过 PnP 删除设备。
- 关闭“2.4G 端点自动显隐”后，端点保持显示。
- 程序退出或下次启动时会尽力恢复端点可见，避免异常退出后留下隐藏端点。
- 提供“恢复音频端点”按钮用于手动恢复。

### 本地运行

- 不需要账号、云同步或遥测。
- 设置保存在本机 `%LOCALAPPDATA%\HecateG2Control\settings.json`。
- 可选开机自启动；关闭主窗口时驻留托盘。

## 支持设备

目前只支持并验证了以下设备：

| 项目 | 值 |
|---|---|
| 耳机 | HECATE G2 无线版 |
| 2.4G 接收器 | `USB\VID_35BB&PID_A209&MI_00` |
| BLE 广播名 | `EDIFIER BLE` |
| BLE 广播服务 | `0000F700-0000-1000-8000-00805F9B34FB` |
| GATT 服务 | `4809F701-1A48-11E9-AB14-D663BD873D93` |
| 通知特征 | `48090001-1A48-11E9-AB14-D663BD873D93` |
| 写入特征 | `48090002-1A48-11E9-AB14-D663BD873D93` |

不同型号可能有不同的 USB VID/PID、BLE 特征、EQ 命令或频段。不要把本项目直接套用到其他 HECATE/Edifier 型号。

## 系统要求

- Windows 10 版本 2004（Build 19041）或更高版本，或 Windows 11。
- x64 Windows。
- 一个正常工作的蓝牙适配器；BLE 控制通道必须可用。
- HECATE G2 的 2.4G 接收器已插入。
- 从源码构建需要 .NET 10 SDK；发布自包含版本不要求目标机器安装 .NET 运行时。
- 通常不需要管理员权限。若端点显隐接口被安全软件或系统策略拦截，程序会报告失败并尝试恢复端点。

## 使用

1. 插入 HECATE G2 的 2.4G 接收器。
2. 打开 Windows 蓝牙，并确认系统能发现 `EDIFIER BLE`。
3. 启动 `HecateG2Control.exe`。
4. 打开耳机并确认其处于 2.4G 模式。
5. 在“2.4G 端点自动显隐”中确认开关已打开。
6. 通过托盘菜单或主窗口切换 EQ。

关闭主窗口默认会隐藏到托盘；要彻底退出，请使用托盘菜单中的“退出”。

如果应用首次启动时耳机已经关机，Windows 可能需要几秒钟确认 BLE 状态，随后隐藏端点。

## 工作原理

### BLE 只用于控制和状态，不承载音频

2.4G 音频走接收器的 USB Audio 接口；BLE 是一条独立的控制/遥测通道。程序维持一个持久的 GATT 会话，并以 GATT 连接状态作为耳机是否在 2.4G 模式的主要依据。

实测中：

- 2.4G 模式：GATT 连接存在。
- 关耳机或切换蓝牙模式：GATT 连接断开。
- `D8` / `F2` 等查询命令的返回值不能用来区分 2.4G 与蓝牙模式。

广告报文用于启动发现和短暂重连兜底；如果蓝牙适配器、距离或驱动导致扫描不稳定，状态判断会受影响。

### 端点显隐而不是切换默认设备

Windows 会一直枚举 2.4G 接收器上的音频端点，即使耳机没有开机。程序使用 Windows Core Audio 的 `IPolicyConfig::SetEndpointVisibility`：

- 在线：`SetEndpointVisibility(endpointId, 1)`
- 离线：`SetEndpointVisibility(endpointId, 0)`

这是未公开的 Windows 接口，不是稳定公开 API。程序会同时检查注册表状态和 Core Audio 状态，并在隐藏失败时回滚为可见。因为这个接口不是官方文档保证的 API，未来 Windows 更新可能导致行为变化。

端点 ID 通过接收器的 `MI_00` USB 音频接口筛选，蓝牙端点不会被隐藏。

## EQ 预设

G2 固件提供的是 4 段硬件 EQ。官方软件中的 10 段曲线包含驱动侧效果；本项目将曲线插值并限幅到 4 个硬件频段，因此是近似曲线，不是官方 DSP 的等价实现。

| 预设 | 100 Hz | 500 Hz | 2 kHz | 8 kHz |
|---|---:|---:|---:|---:|
| 默认 | 0.0 | 0.0 | 0.0 | 0.0 |
| 三角洲 | -3.0 | -2.0 | +3.0 | +2.0 |
| PUBG | -3.0 | +2.0 | +3.0 | -2.0 |
| 无畏契约 | -3.0 | 0.0 | +3.0 | +1.0 |
| APEX | +2.0 | +3.0 | +3.0 | +1.0 |
| CS2 | -3.0 | +3.0 | +3.0 | -2.0 |

## 构建

在仓库根目录运行：

```powershell
dotnet restore PeripheralBattery.slnx
dotnet build PeripheralBattery.slnx -c Release --nologo
dotnet test PeripheralBattery.slnx -c Release --no-build --nologo
```

发布自包含的单文件版本：

```powershell
dotnet publish src/PeripheralBattery.App/PeripheralBattery.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false `
  -o artifacts/HecateG2Control
```

输出目录中的 `HecateG2Control.exe` 可以复制到其他 Windows 机器使用。单文件发布仍会使用 Windows 系统自带的 Core Audio、蓝牙和注册表接口。发布 GitHub Release 时请同时附上 `LICENSE` 和 `THIRD_PARTY_NOTICES*`。

## 项目结构

```text
src/
  PeripheralBattery.App/       Avalonia 界面、托盘、设置、开机启动
  PeripheralBattery.Core/      BLE 协议、EQ 模型和预设
  PeripheralBattery.Windows/   BLE 客户端、Core Audio 和端点显隐
tests/
  PeripheralBattery.Core.Tests/
```

项目内部的 `PeripheralBattery.*` 是早期命名，用户可见的程序名和发布名是 `HecateG2Control`。

## 已明确不做的事情

以下能力没有实现，也不在本项目的目标内：

- 游戏进程启动/关闭监控（避免常驻性能和复杂性问题）。
- 麦克风静音、麦克风增益或系统音量同步。
- 全局 EQ 热键。
- 低电量通知。
- 7.1、动态低频、智能音量、魔法语音等依赖 C-Media 驱动/APO 的功能。
- 用软件模拟耳机实体滚轮的音量控制。

## 已知限制与排障

### 蓝牙适配器不要频繁换 USB 口

在测试中，蓝牙适配器换到另一个 USB 口后出现过大量 `BTHUSB` HCI 错误，导致 BLE 完全扫不到。建议：

- 使用主板后置 USB 口或稳定的外置 USB 口。
- 不要接不稳定的 Hub。
- 不要在没有关机的情况下频繁更换蓝牙适配器接口。

### 端点没有及时隐藏

请先确认：

- 应用仍在托盘运行。
- 自动显隐开关是打开的。
- 蓝牙没有被关闭，`EDIFIER BLE` 能被系统发现。
- 耳机确实处于 2.4G 模式。

状态变化会经过短暂宽限期，避免 BLE 瞬时断连造成端点抖动。若设备一直无法建立 GATT，程序可能保持现状而不是冒险隐藏端点。

### 系统里出现重复的 HECATE 条目

Windows 可能保留旧的音频端点记录。本程序只处理 `MI_00` 对应的 2.4G 音频端点，不会删除蓝牙端点或系统设备记录。如果确实存在旧记录，可以在 Windows 声音设置或设备管理器中手动清理。

### 端点显隐失败

程序的隐藏操作会再次核对注册表和 Core Audio 状态，失败时会尝试恢复为可见。可点击主界面的“恢复音频端点”。如果仍失败，重启 Windows 后重试。

### `IPolicyConfig` 的限制

`IPolicyConfig` 是未公开接口，不保证跨 Windows 版本稳定。项目已把相关代码隔离在 `AudioEndpointVisibilityService`，未来若接口变化，只需要替换这一层实现。

## 参考资料

这些项目帮助理解本地优先的 Windows 设备工具结构、BLE/Edifier 协议和音频端点管理思路。本项目没有直接复制官方应用或驱动代码；协议实现来自对本机和公开资料的独立观察与验证。

- [OpenLogi](https://github.com/AprilNEA/OpenLogi) / [openlogi-net](https://github.com/loxsmoke/openlogi-net) — C# / Avalonia 本地设备工具结构参考
- [HyperEars](https://github.com/silverpoetry/HyperEars) — Edifier BES 协议和电量帧资料
- [edifier-m230-controller](https://github.com/No-22-Github/edifier-m230-controller) — Edifier 自定义 EQ 命令格式参考
- [HaloBattery #179](https://github.com/HeyOkay/HaloBattery/issues/179) — 2.4G 接收器电量通道排查记录

## 依赖与许可

运行时直接依赖：

- [Avalonia](https://avaloniaui.net/) — MIT
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MIT
- Microsoft .NET 10 和 Windows SDK 投影 — 平台 API/运行时

测试项目使用 xUnit（Apache-2.0）等测试依赖，不随应用运行时分发。Avalonia 的传递依赖也遵循各自包许可证；详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

本项目采用 **MIT License**，详见 [LICENSE](LICENSE)。

### 为什么推荐 MIT

这是一个独立的 Windows 桌面工具，不是需要鼓励共享修改的库。MIT：

- 对个人和小型项目最简单；
- 最大程度允许复用和二次开发；
- 与 Avalonia/CommunityToolkit 的 MIT 生态一致；
- 不引入 LGPL 的动态链接/替换库义务。

如果项目未来希望加入明确的专利授权条款，也可以改用 Apache-2.0；LGPL 不适合这个项目的使用场景。

## 免责声明

本项目通过公开可观察的设备和系统行为进行互操作实现，不包含 EDIFIER/HECATE 官方驱动或官方应用代码。使用第三方硬件控制接口可能带来设备设置变化或系统兼容性风险，请自行评估并保留恢复手段。

HECATE 和 EDIFIER 是其各自权利人的商标。本项目与这些权利人没有隶属或背书关系。
