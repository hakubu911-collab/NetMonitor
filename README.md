# 网络流量监测器

一个适用于 Windows 的轻量网络流量、网卡状态和 TCP 连接监视工具。项目提供完整 C# 源码，并按 MIT License 发布。

## 下载使用

从 GitHub Releases 下载 `NetMonitor-v1.0.0-windows.zip`，解压后运行 `NetMonitor.exe`。需要 Windows 10/11。程序依赖 Windows 自带的 .NET Framework 4.x；普通功能不需要管理员权限。

程序会显示网卡实时上传/下载速度、会话与开机累计流量、每日累计流量、IPv4 地址和链路速度。还可显示活动 TCP 连接及对应进程、悬浮窗和托盘图标，并可导出 CSV 报表。

## 隐私与网络行为

程序在本机读取网卡计数器、IPv4 地址、活动 TCP 连接和对应的进程名称/路径。每日累计数据保存在 `%LOCALAPPDATA%\NetMonitorPro\daily_traffic.txt`；CSV 报表由用户点击导出后写入桌面。TCP 视图可能显示远端 IP 和本机进程路径，请在分享报表或截图前检查内容。

为显示网络延迟，程序每秒左右向 `223.5.5.5` 发送 ICMP Ping。源码没有将监测数据上传到服务器的功能，也没有账号或自动更新机制。

## 从源码构建

在 Windows 上安装 .NET Framework 4.x Developer Pack 或 Windows 自带 .NET Framework 编译器，然后在项目目录运行：

```powershell
.\build.ps1
```

生成便携包：

```powershell
.\build.ps1 -Package
```

编译器从系统的 .NET Framework 4.x 目录查找。构建输出放在 `dist/`，不会改动其他安装目录。

## 开源许可

项目依照仓库中的 MIT License 发布。程序使用 .NET Framework 和 Windows 系统 API，没有额外 NuGet 依赖。

