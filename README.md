# DSH 启动器

> 把 `dsh web` 变成托盘常驻的后台服务 —— 从此不用再挂一个 CMD 黑窗。



<p align="center">
  <a href="https://github.com/yachiyoCN/DSH-Launcher/releases/latest"><img src="https://img.shields.io/github/v/release/yachiyoCN/DSH-Launcher?label=%E4%B8%8B%E8%BD%BD" alt="release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6" alt="platform">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.x-512BD4" alt="dotnet">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="license">
</p>

---

## 它是干什么的

平时启动 [DeepSeek Harness](https://github.com/deepseek-ai) 的 Web 界面，得开一个命令行窗口：

```bash
npx @deepseek-ai/dsh web
```

这个窗口**必须一直开着**，一关服务就停，误关一下全部断掉，还白占一个任务栏位置。

**DSH 启动器**把这个过程包起来：双击一次，服务在后台静默运行，托盘留一枚鲸鱼图标，随时右键管理。

## 特性

| | 能力 | 说明 |
|---|---|---|
| 🪟 | **无黑窗** | 编译为 GUI 子系统，全程不弹控制台 |
| 🐳 | **托盘常驻** | 右键菜单管理一切 |
| 🚀 | **开机自启** | 一次勾选，永久生效（写当前用户注册表，免管理员） |
| ♻️ | **崩溃自愈** | 服务异常退出自动重启，最多连续 3 次 |
| 📝 | **日志落盘** | 服务输出全量写入文件，出问题可回溯 |
| 🔒 | **单实例** | 重复双击不会开第二个 |
| 📦 | **零依赖** | 单个 60 KB exe，无需安装运行时之外的东西 |
| ⚙️ | **可配置** | 端口、工作区目录、自动重启都可改 |

## 系统要求

- Windows 10 / 11（x64）
- **.NET Framework 4.x**（Win10 1903 之后系统自带，无需安装）
- 已安装 **Node.js** 与 **DeepSeek Harness**（`dsh` 命令可用）

## 安装

### 方式一：下载现成 exe（推荐）

1. 到 [Releases](https://github.com/yachiyoCN/DSH-Launcher/releases/latest) 下载 `DSHWebLauncher.exe`
2. 放到一个固定的文件夹（比如 `C:\Users\你\Tools\DSH启动器\`）
3. 双击运行，托盘出现鲸鱼图标即成功
4. 右键图标 → 勾选 **开机自启**

### 方式二：自己编译

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:"assets\whale.ico" /resource:"assets\whale.ico",whale.ico `
  /out:"DSHWebLauncher.exe" `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll `
  "src\DshWebLauncher.cs"
```

> 用的是 Windows 自带的 .NET Framework 编译器，**不需要装 .NET SDK 或 Visual Studio**。
> `/target:winexe` 是无黑窗的关键，换成 `exe` 就会弹控制台。

## 用法

双击托盘图标 → 打开界面。右键菜单：

| 菜单项 | 作用 |
|---|---|
| 状态：运行中 PID xxxx · 端口 3080 | 实时状态（只读） |
| 打开 Web UI | 浏览器打开 `http://127.0.0.1:3080/` |
| 重启服务 | 结束进程树后重新拉起 |
| 停止服务 | 停服务但保留托盘 |
| 开机自启 | 勾选/取消 |
| 打开日志目录 | 直达 `%LOCALAPPDATA%\DSHWebLauncher\logs` |
| 退出 | 停服务 + 退出托盘 |

完整教程（配置项、常见问题、排查步骤）见 **[docs/使用说明.md](docs/使用说明.md)**。

## 配置

配置文件：`%LOCALAPPDATA%\DSHWebLauncher\settings.ini`

```ini
NodePath=C:\Program Files\nodejs\node.exe
DshBin=C:\Users\你\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js
Profile=web
Port=3080
WorkDir=C:\Users\你\Documents\deepseek-harness\default-workspace
AutoRestart=1
```

改完重启托盘程序生效。

## 命令行参数

```powershell
DSHWebLauncher.exe --port 3081          # 指定端口
DSHWebLauncher.exe --workdir D:\code    # 指定工作区
DSHWebLauncher.exe --selftest           # 自检：拉起→等端口→报告→退出
DSHWebLauncher.exe --enable-autostart   # 开启自启（脚本用）
DSHWebLauncher.exe --disable-autostart  # 关闭自启
DSHWebLauncher.exe --autostart-status   # 查询自启状态
DSHWebLauncher.exe --status             # 打印当前配置
```

## 工作原理

它只做一件事：把下面这条命令以**隐藏窗口**方式启动，把输出重定向到日志，再用 WinForms 的 `NotifyIcon` 在托盘上挂个图标管理它。

```bash
node "<dsh安装目录>\lib\bin.js" web --port 3080 --no-open
```

所以行为与你在命令行里手敲**完全一致**，只是外面套了层便于管理的壳。

加 `--no-open` 是因为 `dsh web` 默认会自动打开浏览器，而开机自启时不该突然弹窗 —— 要看界面用托盘菜单。

## 项目结构

```
├── src/DshWebLauncher.cs        主程序（C# 5，单文件 ~750 行）
├── assets/whale.ico             7 尺寸图标（16/24/32/48/64/128/256）
├── assets/whale-icon.png        原始 1000×1000 图标源
├── scripts/make-icon.ps1        从 PNG 生成多尺寸 ICO
├── scripts/check-icon.mjs       校验图标是否已嵌入 exe
├── docs/使用说明.md              完整使用教程
└── .github/workflows/build.yml  CI：自动编译 + 发 Release
```

图标嵌了两份：Win32 资源（资源管理器图标）+ .NET 资源（托盘图标按 DPI 取尺寸）。

## 常见问题

见 [docs/使用说明.md 第六节](docs/使用说明.md#六常见问题)，包含：

- 双击后托盘没图标
- 显示"已在运行（外部实例）"
- 开机自启没生效
- 端口被占用
- 日志在哪
- 怎么卸载

## 关于

- 作者：千夏Tesni
- 遇到问题联系 yachi8000p@gmail.com 反馈

## 免责声明

本程序为免费工具。
DeepSeek Harness 本体版权归 DeepSeek 所有，本项目不包含、不修改其任何代码。

## License

[MIT](LICENSE)
