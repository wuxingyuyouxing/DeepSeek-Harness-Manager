# DeepSeek Harness 服务管理器

> 当前版本：**v1.2.2** ｜ 更新记录见 [CHANGELOG.md](CHANGELOG.md)

一个轻量桌面工具（单文件 exe，无任何依赖），用来**一键管理本机的 DeepSeek Harness（`dsh web`）服务**。
浅色/深色/跟随系统三种主题，标准原生窗口（原生最小化/最大化/关闭与动画），带系统托盘常驻，图标为 DeepSeek 官方鲸鱼 Logo。

## 快速开始

双击桌面上的 **「DeepSeek Harness 启动器」** 快捷方式即可打开管理器。

### 核心功能

- **服务状态总览**：运行中 / 已停止 / 启动中 / 端口被占用 / 错误，自动每 2 秒探测；
  显示 PID、端口、启动时间、内存占用。
- **一键操作**：启动 / 停止 / 重启 / 打开浏览器 / 复制访问地址。
- **多实例管理**：左侧列表可添加多个实例（不同端口/地址），右键编辑或删除。
- **日志查看器**：实时滚动显示服务输出，按级别着色（错误红 / 警告黄），一键打开日志文件夹。
- **看门狗**：实例意外退出后自动重启（按实例开关）。
- **系统托盘**：最小化到托盘常驻，托盘菜单可启停服务、双击打开主界面。
- **自检诊断**：检测 node / dsh 版本与路径、DSH_HOME、给出启动命令（含局域网访问提示）。
- **开机自启动**：登录后最小化到托盘（可开关）。
- **明暗主题**：浅色 / 深色 / 跟随系统 三档切换，设置页一键循环。

### 命令行（可选，供脚本/自动化）

```powershell
# 环境自检
DeepSeek-Harness-Manager.exe --cli doctor

# 查询 3080 端口状态（退出码 0 = 运行中）
DeepSeek-Harness-Manager.exe --cli status --port 3080

# 启动 / 停止 / 重启（例如 3099 端口，不会打开浏览器）
DeepSeek-Harness-Manager.exe --cli start --port 3099
DeepSeek-Harness-Manager.exe --cli stop  --port 3099
```

## 文件说明

| 文件 | 作用 |
| --- | --- |
| `DeepSeek-Harness-Manager.exe` | 管理器主程序（单文件，图标已内嵌） |
| `Manager.cs` | 源码（C# 5，可用系统 csc 编译） |
| `build.ps1` | 一键重新编译（无需装任何 SDK） |
| `DeepSeek-Harness.ico` | 官方鲸鱼 Logo 多尺寸图标 |
| `start-dsh.vbs` / `start-dsh.ps1` / `stop-dsh.ps1` | 旧版命令行启动器（保留作后备） |
| `logs\` | 服务日志 + 启动器日志 + 崩溃日志 |
| `config.json` | 实例与设置（首次修改后自动生成） |
| `assets\` | DeepSeek 官方素材（CDN 字标、官网 favicon） |
| `docs\shot-light.png` / `docs\shot-dark.png` | 浅色 / 深色界面截图 |

## 运行要求（分享给他人时）

- **Windows 10/11**（.NET Framework 4.8 系统自带，无需额外安装）。
- **不需要安装 Node.js**：分享包已自带便携版 Node（`runtime\node`），管理器优先使用。
- **兼容各种 dsh 安装方式**（自动识别，无需配置）：
  - 命令行安装（`npx @deepseek-ai/dsh`）：自动从 PATH / npm 缓存找到；
  - **源码仓库本地运行**（如 git clone deepseek-ai/DeepSeek-Harness 后运行）：
    管理器会**从正在运行的实例自动反查**其 node 与 dsh 路径并记住，停止后也能用原路径重启；
  - 均找不到时，「诊断」页可**手动指定** node.exe / dsh 入口路径，或点「一键安装 dsh」（需联网一次）。
- 首次运行会**在程序所在目录自动创建** `config.json` 与 `logs\`，请把程序放在**可写目录**
  （如桌面、D 盘），不要放 Program Files。
- 自签名说明与 SmartScreen 处理见 `dist\数字签名说明.md`。

## 打包分享（dist\ 目录）

| 文件 | 说明 |
| --- | --- |
| `DeepSeek-Harness-Manager-Setup-v1.2.2.exe` | **安装版**（35MB 单文件，类似 QQ/微信）：安装/升级向导、桌面/开始菜单快捷方式、卸载入口；已装用户重跑自动匹配原目录并升级 |
| `DeepSeek-Harness-Manager-Portable-v1.2.2.zip` | **便携免安装版**：解压即用（含自带 Node），无需安装 |
| `数字签名说明.md` | 签名状态与正式签名指南 |
| `CHANGELOG.md` | 版本更新记录 |

便携版解压后直接运行 `DeepSeek-Harness-Manager.exe`；安装版双击 Setup 按向导安装即可。

## 版本约定

每次功能或修复变更：递增版本号（`Manager.cs` 与 `tools\Setup.cs` 中的
`AssemblyVersion`/`AssemblyFileVersion` 同步修改），并在 `CHANGELOG.md` 顶部补充说明；
重建后按标准流程重新打包并签名。

## 从源码构建（仓库克隆后）

```powershell
# 需要：Windows + .NET Framework 4.8（系统自带）+ 首次联网（下载便携 Node，约 34MB）
powershell -ExecutionPolicy Bypass -File build.ps1
```

## 仓库结构

| 路径 | 说明 |
| --- | --- |
| `Manager.cs` | 主程序源码（C# 5，WinForms） |
| `tools/Setup.cs` | 安装器源码（单文件 Setup，嵌入便携包） |
| `build.ps1` / `build-icon.ps1` | 构建脚本（自动准备便携 Node） |
| `runtime/` | 便携 Node.js（.gitignore 排除，构建时生成） |
| `dist/` | 发布产物：安装版 / 便携版 zip（.gitignore 排除，经 GitHub Releases 分发） |
| `docs/` | 界面截图 |
| `assets/` | DeepSeek 官方 Logo 素材（商标，见 LICENSE 说明） |

> 发布二进制请使用 **GitHub Releases**（dist/ 已 gitignore，不进入仓库）。

## 常见问题

- **端口被占用**：状态显示「端口被占用」并给出占用进程 PID；可先关闭占用程序，
  或新建一个实例换端口。
- **首次启动较慢**：`dsh web` 首次运行需解析依赖，最坏约 90 秒，管理器会自动等待。
- **局域网访问**：**DSH 当前版本（0.1.0-rc 系列）出于安全限制不支持局域网绑定**——
  `--host 0.0.0.0` 会被拒绝启动（防止远程代码执行），服务只能监听 `127.0.0.1`，
  其他设备无法访问。管理器会按 DSH 版本如实提示（概览页「局域网访问」卡与诊断页）。
  待 DSH 后续版本放开 `0.0.0.0` 绑定后，管理器将自动恢复局域网访问提示；
  当前确有需求可用反向代理将 `127.0.0.1:<端口>` 暴露到局域网。
- **关闭窗口**：默认最小化到托盘（不会退出）；设置页可改为「关闭即退出」。
  彻底退出请用托盘菜单「退出」。
