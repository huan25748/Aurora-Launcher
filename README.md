# Aurora Launcher

Aurora Launcher 是一个面向 Windows 的 Minecraft 启动器。

本文档以 **1.2.26** 为基线，介绍启动器当前已经具备的功能；后续版本的新功能会逐步补充到 README 中。

项目前身是 Electron 版 Aurora Launcher 1.0.78；本仓库是使用 **.NET 6 + WPF + WebView2** 重写的当前版本。

界面继续复用原有 React 渲染层，下载、安装、Java 管理、账户、启动和实例管理已迁移到 C# 后端。

## 下载

- Gitee Releases：<https://gitee.com/mobai-33460/aurora-launcher/releases>
- 蓝奏云：<https://mobaijianhe.lanzouc.com/b01euviyte>
- 更新日志：[CHANGELOG.md](CHANGELOG.md)

---

## 特性

### 游戏与加载器

- 原版 Minecraft 安装与启动。
- Fabric、Quilt、Forge、NeoForge 安装流程。
- OptiFine 版本下载，支持放入 Forge 实例的 `mods` 目录。
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
- Authlib-Injector 第三方登录。
- 离线账户。
- 账户数据保存在：

```text
<exe目录>/.aurora/accounts.json
```

离线账户与 Microsoft 账户按照 PCL 的语义分别使用自己的 UUID。启动器不会自动把正版存档复制给离线账户，也不会改写世界里的 FTB Teams 数据。

### 联机

- 基于 EasyTier 的 P2P 虚拟局域网。
- 内置公共服务器：

```text
tcp://public.easytier.top:11010
udp://public.easytier.top:11010
```

- 公共服务器延迟检测。
- 支持自定义 EasyTier 服务器地址。
- 支持创建房间、设置网络名称与密码。
- 显示节点信息，便于朋友通过虚拟 IP 连接 Minecraft 局域网世界。

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
- 支持实例复制、导出、导入、删除和整合包导出。

### 下载

- 官方源与 BMCLAPI 自动切换。
- 大文件 Range 分段下载。
- 下载失败重试、连接超时、无数据超时。
- 并发下载客户端、依赖库和资源文件。
- 下载进度按实际步骤显示。
- 支持 Modrinth、CurseForge 搜索、详情、版本与安装。
- 支持本地 `.mrpack` 导入。
- 支持整合包安装与实例创建。

### 内容管理

- 模组。
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
- 版本资讯来自 PCL2 新闻主页 API：

```text
https://news.bugjump.net/apis/versions/latest
```

- 完整更新日志来自 PCL2-NewsHomepage 的版本 Markdown。
- 读取版本 Markdown 文件头中的封面图。
- 点击资讯打开站内阅读弹窗，不直接跳转浏览器。
- 弹窗显示标题、日期、完整正文、来源链接。
- 右上角“更多”打开 Minecraft 官方文章页：

```text
https://www.minecraft.net/zh-hans/article
```

- 普通 Mojang 新闻从官方 `news.json` 合并读取。

### 启动

- 版本 chain 合并。
- classpath 生成。
- 同名依赖去重，保留更高版本。
- natives 解压。
- JVM 参数与游戏参数替换。
- 启动前处理首次启动语言。
- 关闭启动器窗口不会结束正在运行的游戏。
- 游戏日志从 `logs/latest.log` 读取，不再重定向 stdout/stderr。
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

---

## 特别鸣谢

本项目使用了以下镜像、数据接口与开放资源：

### Light-Beacon / PCL2-NewsHomepage

PCL2 新闻主页 API 与版本更新日志数据。

- https://github.com/Light-Beacon/PCL2-NewsHomepage
- https://news.bugjump.net/

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
