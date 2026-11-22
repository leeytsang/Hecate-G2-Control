# HECATE G2 Control

<p align="center">
  <img src="src/PeripheralBattery.App/Assets/app.png" width="96" alt="HECATE G2 Control" />
</p>

<p align="center">
  Windows 上的 HECATE G2 无线耳机控制工具
</p>

<p align="center">
  <a href="https://github.com/leeytsang/Hecate-G2-Control/releases/latest">下载最新版本</a>
  ·
  <a href="https://github.com/leeytsang/Hecate-G2-Control/issues">反馈问题</a>
</p>

## 简介

HECATE G2 Control 是一个面向 **HECATE G2 无线版** 的本地 Windows 工具。它通过耳机的 BLE 控制通道读取电量、写入硬件 EQ，并根据耳机是否处于 2.4G 在线状态，自动显示或隐藏 Windows 中的 2.4G 音频端点。

Windows 会一直保留 USB 2.4G 接收器对应的音频端点，即使耳机已经关机。本项目通过 BLE GATT 连接状态判断耳机是否真正在线，再调用 Windows Core Audio 将对应端点显示或隐藏，让切换体验更接近蓝牙耳机。

> 本项目是社区项目，与 EDIFIER / HECATE 官方没有关联。

## 功能

- 通过 BLE GATT 读取真实电量。
- 托盘菜单显示电量、连接状态和当前 EQ。
- 支持 G2 固件原生 4 段硬件 EQ。
- 内置默认、三角洲、PUBG、无畏契约、APEX、CS2 预设。
- 支持自定义 4 段 EQ，并在写入后读回校验。
- 耳机重新连接后自动恢复上次保存的 EQ。
- 根据耳机连接状态自动显示或隐藏 2.4G 音频端点。
- 托盘右键切换 EQ，不注册全局热键。
- 可选开机自启动。
- 本地运行，无账号、无云同步、无遥测。

## 支持设备

当前仅验证以下设备：

| 项目 | 值 |
|---|---|
| 耳机 | HECATE G2 无线版 |
| 2.4G USB 音频接口 | `USB\VID_35BB&PID_A209&MI_00` |
| BLE 广播名 | `EDIFIER BLE` |
| BLE 广播服务 | `0000F700-0000-1000-8000-00805F9B34FB` |
| GATT 服务 | `4809F701-1A48-11E9-AB14-D663BD873D93` |
| 通知特征 | `48090001-1A48-11E9-AB14-D663BD873D93` |
| 写入特征 | `48090002-1A48-11E9-AB14-D663BD873D93` |

其他 HECATE 或 Edifier 型号可能使用不同的 VID/PID、BLE 特征和 EQ 协议，不能直接套用本项目的设备配置。

## 系统要求

- Windows 10 版本 2004（Build 19041）或更高版本。
- Windows 11。
- x64 Windows。
- 可用的蓝牙适配器。
- HECATE G2 2.4G 接收器。

发布版为自包含单文件程序，目标电脑不需要额外安装 .NET 运行时。从源码构建需要 .NET 10 SDK。

## 安装与使用

### 下载发布版

1. 打开 [Releases](https://github.com/leeytsang/Hecate-G2-Control/releases/latest)。
2. 下载 `HecateG2Control.exe`。
3. 将程序放到固定目录后运行。
4. 插入 HECATE G2 的 2.4G 接收器。
5. 打开 Windows 蓝牙，并确认耳机处于 2.4G 模式。
6. 在控制台中确认“2.4G 端点自动显隐”已开启。

关闭主窗口后程序会驻留托盘。要完全退出，请使用托盘菜单中的“退出”。

### 从源码运行

```powershell
dotnet run --project src/PeripheralBattery.App/PeripheralBattery.App.csproj
```

## 工作原理

### 电量与 EQ

2.4G 音频通过接收器的 USB Audio 接口传输；BLE 是独立的控制和遥测通道。程序维持持久的 GATT 会话，通过它读取电量、查询 EQ 并写入硬件 EQ。

EQ 写入流程为：

1. 读取当前 4 段硬件 EQ。
2. 逐段写入目标值。
3. 再次读取设备 EQ。
4. 只有读回值与目标一致时才判定成功。
5. 写入失败时尝试恢复写入前的 EQ。

### 音频端点显隐

Windows 会持续枚举 2.4G 接收器的音频端点，因此不能只依赖 USB 设备是否存在。

程序使用 `IPolicyConfig::SetEndpointVisibility`：

- 耳机在线：显示接收器 `MI_00` 下的 Render/Capture 端点。
- 耳机离线或切换到蓝牙：隐藏这些端点。

程序只处理匹配 `USB\VID_35BB&PID_A209&MI_00` 的端点，不会隐藏蓝牙音频端点，也不会卸载 USB 设备或驱动。

`IPolicyConfig` 是未公开的 Windows 接口。程序会核对注册表与 Core Audio 状态，失败时尝试回滚为可见状态。

## 配置

设置文件位于：

```text
%LOCALAPPDATA%\HecateG2Control\settings.json
```

其中保存当前 EQ 预设、自定义 EQ、自动显隐开关和缓存的 BLE 地址。

## 构建

```powershell
dotnet restore PeripheralBattery.slnx
dotnet build PeripheralBattery.slnx -c Release --nologo
dotnet test PeripheralBattery.slnx -c Release --no-build --nologo
```

发布自包含单文件版本：

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

发布 GitHub Release 时，请同时附上 `LICENSE` 和 `THIRD_PARTY_NOTICES*`。

## 项目结构

```text
src/
  PeripheralBattery.App/       Avalonia 界面、托盘、设置、开机启动
  PeripheralBattery.Core/      BLE 协议、EQ 模型与预设
  PeripheralBattery.Windows/   BLE 客户端、Core Audio、端点显隐
tests/
  PeripheralBattery.Core.Tests/
```

## 已知限制

- 仅支持并验证 HECATE G2 无线版 `35BB:A209`。
- G2 固件提供的是 4 段硬件 EQ，不等同于官方驱动的 10 段 DSP 效果。
- 不支持 7.1、动态低频、智能音量等依赖 C-Media 驱动或 APO 的功能。
- 不提供麦克风静音、软件音量同步或实体滚轮音量同步。
- 不监控游戏进程，也不注册全局 EQ 热键。
- `IPolicyConfig` 是未公开接口，未来 Windows 更新可能导致兼容性变化。

## 常见问题

### 端点没有及时隐藏

请确认：

- 程序仍在托盘运行。
- 自动显隐开关已打开。
- Windows 蓝牙正常。
- 耳机处于 2.4G 模式。
- `EDIFIER BLE` 能被系统发现。

BLE 状态变化有短暂宽限期，用于避免瞬时断连导致端点反复跳变。

### 找不到 BLE 设备

检查蓝牙适配器是否稳定。测试中更换 USB 接口曾导致 `BTHUSB` HCI 错误，BLE 设备完全无法被发现。建议使用稳定的 USB 接口，并避免通过不稳定的 Hub 连接。

### 系统中有重复的 HECATE 端点

Windows 可能保留旧的音频设备记录。本程序只操作 `MI_00` 对应的 2.4G 端点，不会删除系统设备记录。重复条目可以在 Windows 声音设置中手动清理。

### 端点显隐失败

可以在主界面点击“恢复音频端点”。如果仍然失败，重启 Windows 后重试。

## 隐私

程序完全在本机运行：

- 不连接云端服务。
- 不需要账号。
- 不上传设备数据。
- 不包含遥测。

## 参考资料

- [OpenLogi](https://github.com/AprilNEA/OpenLogi) / [openlogi-net](https://github.com/loxsmoke/openlogi-net)
- [HyperEars](https://github.com/silverpoetry/HyperEars)
- [edifier-m230-controller](https://github.com/No-22-Github/edifier-m230-controller)
- [HaloBattery #179](https://github.com/HeyOkay/HaloBattery/issues/179)

## 许可证

本项目使用 [MIT License](LICENSE)。第三方组件和许可证信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

HECATE 和 EDIFIER 是其各自权利人的商标。本项目与这些权利人没有隶属或背书关系。