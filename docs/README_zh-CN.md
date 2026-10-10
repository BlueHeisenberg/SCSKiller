<p align="center">
  <img width="128" src="../.github/assets/logo.png" alt="SCSKiller 标志">
</p>
<h1 align="center">SCSKiller</h1>
<p align="center">
  <strong>告别着色器编译卡顿。</strong>SCSKiller 是一款面向 Windows 的免费开源游戏着色器预编译工具。
  它会在游戏启动前预先编译着色器，并将结果写入 GPU 驱动程序的缓存，避免游戏过程中因临时编译而卡顿。
</p>

<p align="center">
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases/latest"><img alt="最新版本" src="https://img.shields.io/github/v/release/BlueHeisenberg/SCSKiller"></a>
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases"><img alt="下载量" src="https://img.shields.io/github/downloads/BlueHeisenberg/SCSKiller/total"></a>
  <a href="../LICENSE"><img alt="许可证：GPL-3.0 或更高版本" src="https://img.shields.io/badge/licence-GPL--3.0--or--later-blue"></a>
  <a href="https://www.patreon.com/SCSKiller"><img alt="Patreon" src="https://img.shields.io/badge/Patreon-support-f96854?logo=patreon&logoColor=white"></a>
  <a href="https://discord.gg/st7C4yCTcN"><img alt="Discord" src="https://img.shields.io/badge/Discord-join-5865f2?logo=discord&logoColor=white"></a>
</p>

<p align="center">
  <a href="../README.md">English</a>
  ·
  <a href="./README_zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://scskiller.com">官网</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases/latest">下载</a>
  ·
  <a href="https://www.patreon.com/SCSKiller">Patreon</a>
  ·
  <a href="https://discord.gg/st7C4yCTcN">Discord</a>
  ·
  <a href="https://x.com/SCSKiller">X</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/issues/new?template=bug.yml">反馈问题</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/issues/new?template=game-request.yml">申请游戏支持</a>
</p>

> [!WARNING]
> 唯一的官方下载渠道是本仓库的 [Releases 页面](https://github.com/BlueHeisenberg/SCSKiller/releases)。
> SCSKiller 的官方账号和网站见[官方链接](#官方链接)。

<p align="center">
  <img src="../.github/assets/library.webp" alt="SCSKiller 游戏库：按商店分组展示游戏，以及着色器和管线数量、缓存大小、编译耗时、Needs rebuilding（需要重新编译）或 Warmed（缓存已预热）等状态和 Play（启动游戏）按钮。">
</p>

## 为什么需要它

当游戏首次呈现某种特效时，GPU 驱动程序需要即时编译对应的着色器，当前帧只能等待编译完成。这种短暂的停顿，就是着色器编译卡顿。

SCSKiller 会读取游戏自带的着色器，推断游戏运行时会创建的管线，并在游戏未运行时，通过自身进程提前完成编译，将结果写入驱动程序的着色器缓存。这样，启动游戏时，所需缓存就已经准备好了。

## 实测效果

以下对比驱动缓存为空与使用 SCSKiller 预编译后的表现，每款游戏均游玩约 5 分钟。这里将游戏等待着色器编译达到或超过 20 ms 记为一次卡顿。

<table>
  <tr><th>游戏</th><th>GPU</th><th>测量指标</th><th>驱动缓存为空</th><th>使用 SCSKiller 后</th></tr>
  <tr><td rowspan="4">Final Fantasy VII Rebirth</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>卡顿次数（≥ 20 ms）</td><td>19</td><td><b>0</b></td></tr>
  <tr><td>最长停顿</td><td>76 ms</td><td><b>5 ms</b></td></tr>
  <tr><td rowspan="2">AMD<br>Ryzen AI Max+ 395 (Strix Halo)</td><td>卡顿次数（≥ 20 ms）</td><td>162</td><td><b>0</b></td></tr>
  <tr><td>最长停顿</td><td>291 ms</td><td><b>7 ms</b></td></tr>
  <tr><td rowspan="2">Silent Hill: Townfall</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>卡顿次数（≥ 20 ms）</td><td>24</td><td><b>0</b></td></tr>
  <tr><td>最长停顿</td><td>347 ms</td><td><b>8 ms</b></td></tr>
  <tr><td rowspan="4">Hogwarts Legacy</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>卡顿次数（≥ 20 ms）</td><td>1,014</td><td><b>87</b></td></tr>
  <tr><td>最长停顿</td><td>2,400 ms</td><td><b>100 ms</b></td></tr>
  <tr><td rowspan="2">AMD<br>Ryzen AI Max+ 395 (Strix Halo)</td><td>卡顿次数（≥ 20 ms）</td><td>27,743</td><td><b>206</b></td></tr>
  <tr><td>最长停顿</td><td>2,146 ms</td><td><b>104 ms</b></td></tr>
  <tr><td rowspan="2">Tiny Tina's Wonderlands</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>卡顿次数（≥ 20 ms）</td><td>583</td><td><b>0</b></td></tr>
  <tr><td>最长停顿</td><td>315 ms</td><td><b>13 ms</b></td></tr>
  <tr><td>Star Wars Jedi: Survivor</td><td>NVIDIA<br>RTX 5090</td><td>卡顿次数（≥ 20 ms）</td><td>9,038</td><td><b>11</b></td></tr>
</table>

数据通过 SCSKiller 的管线采集器测得。你的测试结果会因游戏、GPU 和驱动程序而异。

- **可解决的问题：** 着色器编译卡顿。
- **无法解决的问题：** 场景探索、资源流式加载以及其他原因导致的卡顿。
- **偶尔会出现：** 程序未能找到或采集到的着色器仍可能在游戏内编译。

## 功能

- **支持 NVIDIA 和 AMD。** 适用于 DirectX 12 游戏，以及 NVIDIA 平台上的 DirectX 11 游戏。
- **自动发现已安装的游戏。** 支持 Steam、Epic Games、EA app、GOG、Ubisoft Connect、Xbox (PC)、Battle.net、PURPLE、HoYoPlay 和 Gaijin。
- **识别容易卡顿的游戏。** 游戏库会将已知存在卡顿问题的游戏置顶，并说明原因。
- **驱动更新后重新编译。** 驱动更新会清空着色器缓存，SCSKiller 能检测到这一变化并重新编译；经你允许，也可以自动完成。
- **可选采集器：** 适用于无法通过文件推断出管线的游戏。开启采集器后游玩几分钟，再进行编译。它还可以与替换着色器的模组配合采集数据。
- **查看每次游玩的帧时间。** 启用采集器后，游戏详情页会展示上次游玩的帧时间图表，区分着色器编译卡顿和其他卡顿。帧时间数据仅保存在本机。
- **一键启动游戏。** 游戏库和游戏详情页均提供启动游戏按钮，可通过对应商店启动游戏。
- **面向 Patreon 支持者的社区着色器哈希数据库。** 借助其他玩家的采集数据，无需先自行采集即可为游戏预编译。分享自己的数据完全自愿，且为匿名分享，内容仅包含着色器哈希值。
- **不干预带反作弊系统的游戏，仅读取其文件。** 不启动游戏，不注入代码，也不安装采集器。唯一例外是 ELDEN RING 和 ARMORED CORE VI 的离线采集：在不运行 EasyAntiCheat 的情况下离线游玩。此功能须按游戏单独启用，每次启动前都需确认，风险由用户自行承担。

## 安装

| 要求 | |
|---|---|
| 操作系统 | Windows 10（2004 版本）或 11，64 位 |
| 显卡 | NVIDIA 或 AMD |
| 游戏 | DirectX 12；NVIDIA 显卡支持 DirectX 11 |

暂不支持 Intel 显卡，因为我没有可用于测试的设备。如果你愿意赞助一块 Intel Arc 显卡，请通过
[contact@scskiller.com](mailto:contact@scskiller.com) 与我联系。

从 [Releases 页面](https://github.com/BlueHeisenberg/SCSKiller/releases/latest) 下载并运行 `SCSKiller-Setup.exe`。程序仅为当前用户安装，无需管理员权限，并支持自动更新。

也可以下载 `SCSKiller-<version>-Portable.zip`，解压到任意有写入权限的文件夹，然后运行 `SCSKiller.exe`。便携版同样支持自动更新。删除便携版文件夹前，请先关闭各游戏的采集功能，让程序移除游戏目录中的采集器文件。

<!-- UNSIGNED NOTICE: delete this block once releases are signed. -->
> [!IMPORTANT]
> **发布版本目前尚未进行代码签名**，因此首次运行 SCSKiller 时，Windows SmartScreen 很可能会提示“Windows 已保护你的电脑”。请点击“更多信息”，然后选择“仍要运行”。此提示在每次安装时仅出现一次：更新由 SCSKiller 自行下载，绝不会再次触发该提示。
>
> 通过 SignPath Foundation 进行代码签名的工作正在进行中。如需验证下载文件，请将其 SHA-256 哈希值与发布说明中的值进行比较（PowerShell 命令：`Get-FileHash <file>`）。智能应用控制（Windows 11）会直接阻止未签名的应用程序；如果已启用该功能，请等待完成签名的发布版本。
<!-- END UNSIGNED NOTICE -->

## 使用方法

1. **打开 SCSKiller。** 程序会自动发现已安装的游戏及其自带的着色器。
2. **将游戏加入队列，点击开始预编译。** 可以先点击添加所有推荐游戏。编译期间请勿启动游戏。
3. **开始游戏。** 可以从游戏商店启动，也可以点击 SCSKiller 中对应游戏的启动游戏按钮，由程序通过商店启动。之后可以关闭 SCSKiller，游戏会自行读取驱动缓存。也可以让 SCSKiller 在游玩期间保持运行，以检查编译结果是否写入了游戏实际使用的缓存；如果同时启用了采集器，游玩结束后，游戏详情页还会展示本次游玩的帧时间，以及期间是否仍发生着色器编译。

如果某款游戏提示需要采集，请点击开始采集，游玩几分钟后再进行编译。

<p align="center">
  <img src="../.github/assets/queue.webp" width="49%" alt="编译队列：一款游戏正在编译，并显示每秒编译的管线数；其他游戏正在等待，另有一款已编译完成。">
  <img src="../.github/assets/detail.webp" width="49%" alt="游戏详情页：展示上次游玩的帧时间，分别标记着色器编译卡顿、其他卡顿和加载过程，并列出耗时较长的帧。">
</p>

## 常见问题解答

**用于带反作弊系统的游戏是否安全？** SCSKiller 不会启动这类游戏、向其注入代码或安装采集器，只会读取游戏文件。唯一例外是离线采集：ELDEN RING 和 ARMORED CORE VI 可通过直接运行游戏可执行文件，在不运行 EasyAntiCheat 的情况下离线游玩。此功能须在各自的游戏详情页单独授权，每次启动前都需确认，风险由用户自行承担。SCSKiller 会向游戏文件夹添加 `d3d12.dll`、`scskiller.ini`、`scskiller.armed` 和 `steam_appid.txt`，再直接启动游戏可执行文件（离线运行，不启动 EasyAntiCheat）。采集器只采集这个游戏进程；通过其他方式启动时，包括通过 Steam 启用 EasyAntiCheat 启动时，均不会采集任何内容。Steam 必须保持运行，离线模式也可以。游戏退出后，SCSKiller 会立即移除上述文件，保留采集数据和本次游玩的帧时间，并确认游戏文件夹已恢复原状；即使中途关闭了 SCSKiller，也会执行清理和检查。若发生崩溃或断电，则会在下次登录系统或启动 SCSKiller 时完成清理和检查。如果这些文件尚未移除就以在线模式启动游戏，账号可能遭到封禁。

**会写入哪些内容，保存在哪里？** 设置和各游戏的编译计划保存在 `%LOCALAPPDATA%\SCSKiller\`，编译后的管线保存在驱动程序自身的着色器缓存中。SCSKiller 不会修改游戏原有文件。只有启用可选的采集器时，才会向对应游戏的文件夹添加 `d3d12.dll`、`scskiller.ini` 和 `scskiller.armed`；关闭采集功能时，只移除这三个文件。`scskiller.armed` 表示已检查该游戏安装目录是否含有反作弊组件；安装目录中的任何内容一旦发生变化，SCSKiller 就会删除此文件。采集器会在游戏启动时决定是否采集：只有该文件当时存在，才会采集本次游玩数据；已经开始的采集则会持续到游戏退出。卸载 SCSKiller 时，会从所有游戏文件夹中清除上述文件，以及采集器写入的采集数据。

**它是否会修改我的驱动程序或其设置？** 不会。它通过 DirectX 进行编译，方式与游戏本身相同。

**我是否需要创建账户？** 不需要。本应用在你电脑上执行的所有操作均为免费。通过 Patreon 登录只会解锁支持者功能。

**游戏未被识别，或者遇到了问题怎么办？** 请使用问题反馈或游戏支持申请模板提交 [issue](https://github.com/BlueHeisenberg/SCSKiller/issues/new/choose)。

## 支持项目

本应用免费，并将一直保持免费。[Patreon](https://www.patreon.com/SCSKiller) 赞助用于支付社区数据库的服务器费用：

| 赞助等级 | 权益 |
|---|---|
| **Patreon supporter** | 社区着色器哈希数据库，以及 Beta 测试版 |
| **Patreon backer** | 上述全部权益，以及 Alpha 测试版和游戏支持申请的优先处理 |

在应用的 Settings（设置）中使用 Patreon 登录，即可解锁对应等级的权益。

## 从源码构建

需要 Visual Studio 2022（安装包含 CMake 的 C++ 桌面开发工作负载）以及 .NET 10 SDK：

```
cmake -S proxy -B proxy/build -A x64
cmake --build proxy/build --config Release
dotnet build SCSKiller.slnx -c Release
```

`build/publish.ps1` 会构建完整的发行包，并输出到 `dist\` 目录。详细实现原理见 [ARCHITECTURE.md](../ARCHITECTURE.md)，
贡献指南见 [CONTRIBUTING.md](../CONTRIBUTING.md)，更新记录见 [CHANGELOG.md](../CHANGELOG.md)。
如需报告安全问题，请遵循 [SECURITY.md](../SECURITY.md) 中的指引，切勿提交公开 issue。

## 官方链接

- GitHub: https://github.com/BlueHeisenberg/SCSKiller
- 网站: https://scskiller.com
- Patreon: https://www.patreon.com/SCSKiller
- Discord: https://discord.gg/st7C4yCTcN
- X: https://x.com/SCSKiller

除上述渠道外，任何以 SCSKiller 名义出现的账号或网站均非官方所有。如有发现，请
[告知我们](https://github.com/BlueHeisenberg/SCSKiller/issues/new/choose)。

## 许可证

本项目采用 GPL-3.0-or-later 许可证（见 [LICENSE](../LICENSE)），并针对 Oodle、Windows App SDK 和显卡驱动库提供额外许可（见 [LICENSE-EXCEPTION.txt](../LICENSE-EXCEPTION.txt)）。
第三方组件声明见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)，代码签名政策见 [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md)。

本项目与任何 GPU 制造商、游戏引擎开发商或游戏发行商均无关联。所有产品名称均为其各自所有者的商标。

### 衍生版本（Fork）

欢迎在遵守许可证的前提下创建衍生版本。请为衍生版本使用独立名称，注明其为基于 SCSKiller 的非官方版本，并保留 SCSKiller 的版权声明。本 README 中的测试数据和截图均来自 SCSKiller，请勿将其作为衍生版本自身的测试结果或截图展示。

官方构建仅在本仓库的 [Releases 页面](https://github.com/BlueHeisenberg/SCSKiller/releases) 提供。衍生版本的构建、缺陷以及涉及反作弊系统的行为均由其作者负责：SCSKiller 不会向带反作弊系统的游戏部署采集器，也无法为改变这一行为的衍生版本担保。

欢迎通过 PR 将改进贡献回本项目。来自衍生版本的问题修复、游戏适配和建议，都能让所有 SCSKiller 用户受益。
