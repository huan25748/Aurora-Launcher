# Aurora Launcher

Aurora Launcher 是一个面向 Windows 的 Minecraft 启动器。

本文档以 **2.1.2** 为基线，介绍启动器当前已经具备的功能。

项目前身是 Electron 版 Aurora Launcher 1.0.78；本仓库是使用 **.NET 6 + WPF + WebView2** 重写的当前版本。

界面继续复用原有 React 渲染层，下载、安装、Java 管理、账户、启动和实例管理已迁移到 C# 后端。

## 下载

- Gitee Releases：<https://gitee.com/mobai-33460/aurora-launcher/releases>
- 蓝奏云：<https://mobaijianhe.lanzouc.com/b01euviyte>
- 更新日志：[docs/CHANGELOG.md](docs/CHANGELOG.md)

---

## 特性

### 游戏与加载器

- 原版 Minecraft 安装与启动。
- Fabric、Quilt、Forge、NeoForge 安装流程。
- OptiFine 支持独立版本安装；实例已有 Fabric / Quilt / Forge / NeoForge 时只把 jar 放入 `mods` 目录，并提供自动 / BMCLAPI / 官方三种下载源。
- 原版下载页支持：

```text
正式版
快照
愚人节
远古版本
全部
```

- 愚人节版本同时支持已知历史版本和“快照 + 4 月 1 日”自动识别。
- 版本清单支持 Mojang 官方与 BMCLAPI 镜像。

### Java 管理

- 扫描启动器内置 runtime、HMCL/PCL 的 `mojang-jre-legacy`、JAVA_HOME、PATH、常见 JDK 目录。
- 支持下载 Mojang Java 运行时。
- 下载后检查 `bin/java.exe`、`lib/rt.jar`、`lib/modules`，避免使用不完整运行时。
- 按游戏版本、Forge 版本和 OptiFine 选择 Java。
- 包括：

```text
Forge 1.16（34.0.0 ～ 36.2.25）最高 Java 8u321
Forge 1.16（36.2.26+）按 Minecraft 版本要求
Forge 1.12 及以下最高 Java 8
Forge 1.13 ～ 1.14  Java 8 ～ 10
Forge 1.15          Java 8 ～ 15
Minecraft 1.17      至少 Java 16
Minecraft 1.18+     至少 Java 17
Minecraft 1.20.5+   至少 Java 21
```

### 账户

- Microsoft 账户：OAuth 设备代码登录。
- Authlib-Injector 第三方登录，内置 LittleSkin 预设，也可填写其他验证服务器。
- 离线账户。
- 账户数据保存在：

```text
<exe目录>/.aurora/accounts.json
```

离线账户与 Microsoft 账户按照 PCL 的语义分别使用自己的 UUID。启动器不会自动把正版存档复制给离线账户，也不会改写世界里的 FTB Teams 数据。

### 联机

- 基于 EasyTier 的 P2P 虚拟局域网。
- 内置公共服务器，可自选节点、检测延迟，也可以添加自定义服务器：

```text
tcp://public.easytier.top:11010
udp://public.easytier.top:11010
```

- 公共服务器延迟检测。
- 支持自定义 EasyTier 服务器地址。
- 创建房间后生成邀请码，朋友粘贴邀请码即可进入同一个虚拟局域网。
- 显示房间成员、房主虚拟 IP 与局域网端口，可扫描局域网并复制地址。
- 支持修复防火墙；Windows 首次使用可能需要允许 TUN 驱动或管理员权限。
- 陶瓦联机（Terracotta）：联机页可与 EasyTier 自由切换。
- 陶瓦联机与 HMCL 使用同一套房间码，两边创建的房间可以互相加入。
- 陶瓦联机支持一键安装、创建房间、加入房间、成员列表与备用直连地址。

### 实例

- 每个实例拥有独立的 `instance.json`。
- 新安装的游戏生成自包含版本目录。
- 导入已有版本时直接引用原目录：

```text
instance.gameDir = 原版本目录
```

不会复制 mods、saves、config，也不会产生重复存档。

- 每次启动启动器都会扫描游戏目录下的 `versions`。
- 自动导入尚未登记的版本。
- 已导入实例会检查加载器类型：

```text
neoforge
forge
fabric
quilt
vanilla
```

- 之前被误判为原版的 NeoForge 实例会自动修正。
- 支持实例复制、导入、删除和整合包导出。
- 整合包导出按 PCL 流程生成：复制实例内容到 `overrides/`，对 jar/zip 计算 SHA1 与 Murmur2 指纹，用 Modrinth / CurseForge 反查下载地址。
- 已被识别的文件写入 `modrinth.index.json` 的 `files[]`，不重复打包；只有配置、脚本与未识别文件进入 `overrides/`。
- 导出格式支持 `.mrpack` 与 `.zip`，内容相同，仅扩展名不同。

### 下载

- 官方源与 BMCLAPI 自动切换。
- 大文件 Range 分段下载。
- 下载失败重试、连接超时、无数据超时。
- 并发下载客户端、依赖库和资源文件。
- 下载进度按实际步骤显示。
- 支持 Modrinth、CurseForge 搜索、详情、版本与安装。
- 模组 / 整合包与游戏本体使用分开的分段策略：
- 模组 CDN：256KB 起允许分段，小于等于 1MB 用 64KB 一段，1～8MB 用 256KB 一段。
- 游戏本体 / 加载器：小于 4MB 单连接，4MB 起 1MB 一段，64MB 起最多 640 段。
- 按下载主机检测是否支持 Range，不支持时自动降级为单连接，避免 404 重试。
- 下载核心对齐 PCL：小文件阈值 4MB、8MB 起步分段、单文件最多 12 条连接、全局最多 64 条连接。
- 慢段自动对半拆分（8 秒低于 50KB/s 时），拆分后只写剩余区间，已下载部分不重复。
- 下载源设置对下载候选与版本列表生效；Modrinth 支持自动、官方、镜像，分类下载源可按原版 / Forge / NeoForge / Fabric / Quilt 单独指定。
- Fabric API 版本列表由 Modrinth 查询并按实例 MC 版本精确过滤，下载优先 CurseForge，失败回退 Modrinth。
- 支持本地 `.mrpack` 导入。
- 支持整合包安装与实例创建：把 .zip / .mrpack / .curseforge 整合包直接拖进窗口任意位置即可安装，拖入时窗口变暗并提示「松开安装整合包」，松开后自动识别格式并导入。
- 也支持把整合包拖到 exe / 快捷方式上，或通过「打开方式」选择启动器，用启动参数导入。
- 拖放由注册在 WebView2 子窗口上的 OLE 接收器处理，不受 WebView2 遮挡影响。

### 内容管理

- 模组。
- 安装模组前解析前置依赖，弹窗列出必装与可选前置，可勾选一并下载；已安装按模组身份判断，版本不同也算已安装。
- 勾选可选依赖时自动带出它自己的必装前置。
- 模组详情提供「在 MC百科 查看」，按中文名、英文名及常见写法依次查询。
- 资源包。
- 光影。
- 数据包。
- 世界。
- 截图。
- 服务器列表。
- 日志。
- 实例设置。

### 资讯

- 首页显示最新正式版与最新预览版。
- 版本列表来自 Mojang 官方版本清单，与下载页共用同一份数据。
- 版本正文与配图来自 Mojang 官方 `javaPatchNotes` 与 minecraft.net 文章。
- 普通新闻读取 minecraft.net 首页 LATEST NEWS 网格的第一条。
- 点击新闻卡片直接在站内显示完整正文，不跳转浏览器。
- 右上角「更多」打开 Minecraft 官方文章页：

```text
https://www.minecraft.net/zh-hans/article
```

- 刷新按钮点击后禁用并显示「刷新中…」，成功或失败都会提示。


### 启动

- 版本 chain 合并。
- classpath 生成。
- 同名依赖去重，保留更高版本。
- natives 解压。
- JVM 参数与游戏参数替换。
- 启动前处理首次启动语言。
- 关闭启动器窗口不会结束正在运行的游戏。
- 启动后自动把游戏进程设为「高」优先级，并为对应 java.exe 写入高性能 GPU 偏好。
- 启动前自动做一次内存优化：整理启动器内存，并回收当前用户可访问进程的闲置工作集。
- 同时捕获游戏 stdout/stderr 并读取 `logs/latest.log`，进程退出后停止监听。
- 崩溃分析支持：

```text
Java 版本与 Forge 不兼容
Java 运行时损坏
log4j / ModLauncher 签名不匹配
常见缺失前置与类加载错误
```

### 界面

- WPF 无边框窗口。
- WebView2 加载本地界面。
- 深色 / 浅色主题。
- 实例卡片封面与加载器图标。
- 窗口缩放、拖动、最小化、最大化。
- 控制台默认收起。

### 其他

- 更新检查：读取 Gitee Releases，支持直接下载并重启。
- 存储占用统计。
- 缓存与安装器残留清理。
- 未使用依赖库与资源精简。
- 本地日志与操作记录。
- 游戏时长统计：游戏退出时累加到实例，首页显示总时长。

---

## 目录结构

```text
<exe目录>/
├─ .aurora/
│  ├─ config.json
│  ├─ accounts.json
│  ├─ logs/
│  ├─ cache/
│  └─ app/
└─ .minecraft/
   ├─ versions/
   ├─ libraries/
   ├─ assets/
   ├─ runtime/
   ├─ instances/
   └─ saves/
```

配置、账户和日志与游戏数据分离，移动启动器目录时可整体迁移。

---

## 源码结构

```text
src/
├─ Core/       IPC、资源包、日志、进度、路径
├─ Accounts/   微软与第三方账户、皮肤与披风
├─ Download/   CurseForge / Modrinth / 多源下载
├─ Instances/  实例、加载器、Java、整合包导入导出
├─ Network/    服务器 Ping、局域网扫描、联机
├─ System/     系统信息、内存优化、更新、新闻
└─ Launch/     启动与版本链

renderer/      界面资源（打包进 assets.zip）
web/           WebView 桥接脚本
lib/           WebView2 SDK 程序集
assets/        静态资源
docs/          文档与更新日志
release/       发布产物
```

---

## 运行要求

- Windows 10 / Windows 11
- .NET 6 Desktop Runtime
- WebView2 Runtime

发布版是框架依赖单文件，不包含 .NET 运行时。

---

## 构建

```powershell
dotnet build -c Release -p:UseSharedCompilation=false

dotnet publish -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o single
```

界面资源需要重新打包：

```text
renderer/
web/aurora-shim.js
→ assets.zip
```

`assets.zip` 会作为嵌入资源打进 exe，首次运行时解压到 `.aurora/app`。

`lib/` 下需要保留 WebView2 SDK 程序集。

---

## 项目状态

- 原版、Fabric、Quilt、Forge、NeoForge 安装流程已接入。
- Forge 1.16.5 的完整安装与启动已实机验证。
- NeoForge、Forge 其他版本的兼容矩阵仍在持续验证。
- 离线账户与 Microsoft 账户的存档按 UUID 独立，不提供跨账户自动迁移。
- 当前主要面向 Windows x64。
- 2.0.0 起，安装、下载、内容管理、陶瓦联机与整合包导出进入稳定版本线。

---

## 特别鸣谢

本项目使用了以下镜像、数据接口与开放资源：

### bangbang93 / BMCLAPI

提供 BMCLAPI 镜像源与 Forge 安装工具。

- https://bmclapi.bangbang93.com/

### MC 百科

提供模组中文名称与相关资料。

- https://www.mcmod.cn/

### z0z0r4 / MCIM

提供社区资源镜像与帮助库。

- https://www.mcimirror.top/

### Modrinth

提供模组、整合包、资源包、光影的数据与下载接口。

- https://modrinth.com/

### CurseForge

提供模组与整合包数据接口。

- https://www.curseforge.com/

### MyMemory

提供中文搜索词转英文的免费翻译接口。

- https://mymemory.translated.net/

### EasyTier

提供 P2P 虚拟局域网与联机底层。

- https://github.com/EasyTier/EasyTier
- https://easytier.rs/

### Terracotta / 陶瓦联机

提供陶瓦联机的二进制与 HTTP 接口协议。

- https://github.com/burningtnt/Terracotta
- https://burningtnt.github.io/Terracotta/

### PCL / PCL-CE

Plain Craft Launcher 项目作为 Java 选择、账户语义、安装与启动行为的参考。

感谢以上项目与服务的作者和贡献者。

---

## 许可证

本项目使用 **GNU Lesser General Public License v3.0 only**（LGPL-3.0-only）授权。

完整许可证文本见：[LICENSE](LICENSE)

LGPL-3.0 包含 GNU GPL v3.0 的条款：

- <https://www.gnu.org/licenses/lgpl-3.0.txt>
- <https://www.gnu.org/licenses/gpl-3.0.txt>

---

## 免责声明

本项目与 Mojang Studios、Microsoft 无隶属关系。

Minecraft 是 Mojang Studios 的商标。
