# Clawd for Orca

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md) · **简体中文**

Clawd 是一款适用于 macOS 和 Windows 的桌面宠物，用于关注在 [Orca](https://www.onorca.dev) 中运行的编码 Agent。当 Agent
等待批准或回复时，它会在屏幕上显示卡片提醒；无需打开 Orca，即可直接批准、回复并操作终端。

https://github.com/user-attachments/assets/1c0bdd06-f9ae-479c-bc65-6bfe3ec750a1

> **非官方项目。** 与 Anthropic、Stably AI(Orca)无关。Claude、Claude Code 和 Clawd 是 Anthropic 的商标，
> Orca 是 Stably AI 的产品。

## 为什么选择 Clawd

Agent 工作期间，你可以专注于其他事情。需要批准或回复时，Clawd 会在屏幕上显示卡片，不会错过任何请求。

通过卡片即可在不打开 Orca 的情况下批准或回复，并在同一处查看 Agent 的进度和终端。这是它与只提供
“批准”按钮的通知应用的区别。

Clawd 在各平台上都是原生应用：macOS 版使用 Swift，Windows 版使用 WinUI 3 编写。Mac 版下载大小 3.7 MB，
空闲时 CPU 占用约 1%。

## 下载

### macOS

从 [GitHub Releases](https://github.com/joonseo1227/clawd-for-orca/releases) 下载 `Clawd-1.1.0-macOS.dmg`，
打开后将 `Clawd.app` 拖到“应用程序”文件夹。该应用已通过 Apple 公证。

需要 macOS 15 或更高版本的 Apple silicon Mac，以及 [Orca](https://www.onorca.dev)。

![打开聊天弹出窗口、位于桌面上的 Clawd](docs/images/overview-popover-en.png)

### Windows

从 [GitHub Releases](https://github.com/joonseo1227/clawd-for-orca/releases) 下载 `Clawd-1.1.0-Windows-x64.msi`
并运行。无需管理员权限，会安装到当前用户并添加到“开始”菜单。安装程序没有代码签名，SmartScreen 可能会要求确认。

需要 x64 版 Windows 11 和 Windows 版 [Orca](https://www.onorca.dev)。安装方法和已知限制请参阅
[Windows/README.md](Windows/README.md)(英文)。

![在 Windows 桌面上打开聊天的 Clawd](docs/images/overview-windows-en.png)

## 主要功能

- **提醒卡片:** Agent 等待批准或回复时以卡片提醒，并在你处理之前重复提醒。
- **聊天:** 按状态列出 Agent，并逐步显示 Claude Code 的工作过程。
- **权限批准:** 以按钮显示批准请求，一键(⌘1 至 ⌘3)即可回应。
- **回复:** 在聊天中输入的消息会直接发送到 Agent 的终端。
- **实时终端:** 连接 Orca 后，可直接使用 Agent 的终端，包括颜色和输入。
- **拖放文件:** 将文件拖到 Clawd 或聊天中即可发送文件路径。
- **菜单栏、快捷键(⌃⌥J)和系统通知:** 也可以在通知中直接回复。

| macOS | Windows |
| :---: | :---: |
| <img src="docs/images/overview-card-en.png" width="420" alt="在 macOS 上举着 “Permission needed” 卡片的 Clawd"> | <img src="docs/images/overview-windows-card-en.png" width="420" alt="在 Windows 上举着 “Permission needed” 卡片的 Clawd"> |

应用支持英语和韩语，并跟随系统语言。

## 使用方法

| 操作 | 结果 |
| --- | --- |
| 点击 Clawd 或菜单栏图标 | 打开聊天，优先显示正在等待的 Agent。 |
| ⌃⌥J | 在任何地方打开或关闭聊天。 |
| ⌘1 至 ⌘3 | 回应屏幕上的权限请求。 |
| ⌘T | 在聊天和终端之间切换。 |
| 双击 Clawd | 在 Orca 中打开最需要处理的 Agent。 |

在 Windows 上，请用 Ctrl 代替 ⌘，用 Ctrl+Alt+J 代替 ⌃⌥J，并点击通知区域图标代替菜单栏图标。

### 连接实时终端

1. 在 Orca 左侧边栏打开 **Orca Mobile**。
2. 生成二维码并点击 **Copy pairing code**。
3. 在 Clawd 设置中点击实时终端旁的 **Pair…**，然后粘贴代码。

## 更新

Clawd 每天在 GitHub 上检查一次新版本。在 macOS 上，它会通过卡片和菜单提醒你，由你决定何时安装。在 Windows
上，它会在后台下载更新，然后询问是否重新启动 Clawd 进行安装。你可以在设置中立即检查，或关闭自动检查。

## 隐私

Clawd 与同一台电脑上运行的 Orca 通信。除此之外唯一的连接是每天一次的更新检查：它只从 GitHub 读取 Clawd
的发布信息，不会发送任何关于你或你的 Agent 的信息。配对信息仅保存在 macOS 钥匙串或 Windows 凭据管理器中。

## 从源码构建

```sh
git clone https://github.com/joonseo1227/clawd-for-orca.git
cd clawd-for-orca/macOS
./build.sh --install
```

构建 Mac 版需要 Xcode 26 或更高版本。Windows 版请参阅 [Windows/README.md](Windows/README.md)(英文)。
测试、签名和工作原理请参阅 [CONTRIBUTING.md](CONTRIBUTING.md)(英文)。

## 许可证

[GPL-3.0](LICENSE) © 2026 joonseo1227。你可以自由使用、修改和分享代码，但基于它分发的程序也必须连同源代码
以 GPL-3.0 发布。许可证不适用于上述商标，[`macOS/Sources/Sprite.swift`](macOS/Sources/Sprite.swift) 中绘制的
Clawd 角色归 Anthropic 所有。
