# Aurora Launcher

> 一款为 Windows 打造的 Minecraft 启动器。  
> 以 .NET 6 + WPF 为骨架，以 WebView2 承载界面，把安装、账户、下载、实例与联机收进同一套工程。

<p align="center">
  <img alt="version" src="https://img.shields.io/badge/version-2.4.1-6C8CFF?style=flat-square">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-1F2430?style=flat-square">
  <img alt="runtime" src="https://img.shields.io/badge/.NET-6.0-512BD4?style=flat-square">
  <img alt="license" src="https://img.shields.io/badge/license-LGPL--3.0--only-2EA043?style=flat-square">
</p>

---

## 目录

- [设计取向](#设计取向)
- [能力概览](#能力概览)
- [安装与运行](#安装与运行)
- [工程结构](#工程结构)
- [关键实现](#关键实现)
- [数据布局](#数据布局)
- [构建](#构建)
- [兼容性](#兼容性)
- [致谢](#致谢)
- [许可证](#许可证)

---

## 设计取向

Aurora 不做“把网页套进窗口”的启动器。

界面层保留 React 的渲染能力，但所有会改变磁盘、网络与进程状态的动作都落在 C# 后端：

```text
React 渲染层
    │  postMessage / invoke
    ▼
C# Bridge（类型化命令）
    │
    ├─ 安装与版本链
    ├─ 账户与令牌
    ├─ 下载与镜像调度
    ├─ 实例与整合包
    └─ 进程与联机
```

这条边界带来三个直接结果：

- 界面可以独立演进，不必把业务逻辑写进前端；
- 下载、安装、启动可以复用同一套错误处理与日志；
- 启动器崩溃时，游戏进程与磁盘状态不会被前端一并带走。

设计上遵循两条原则：

1. **行为对齐成熟实现。** 账户语义、Java 选择、整合包导出等高风险路径，以 PCL / HMCL 的既有行为为基准，而不是另造一套“看起来差不多”的逻辑。
2. **只修根因。** 不用层层重试和兜底掩盖问题；能用日志证明的，不靠猜测。

---

## 能力概览

| 模块 | 能力 |
| --- | --- |
| 游戏安装 | 原版、Fabric、Quilt、Forge、NeoForge、OptiFine |
| 账户 | Microsoft OAuth 设备代码、Authlib-Injector、离线账户 |
| Java | 运行时扫描、Mojang Java 下载、按版本与加载器匹配 |
| 下载 | 官方 / BMCLAPI 自动切换、Range 分段、并发与慢段拆分 |
| 内容 | Modrinth / CurseForge 搜索、详情、版本、依赖解析与安装 |
| 实例 | 独立 `instance.json`、导入、复制、删除、版本链合并 |
| 整合包 | `.mrpack` / `.zip` 导入导出，拖放安装 |
| 联机 | 陶瓦联机（Terracotta），与 HMCL 房间码互通 |
| 诊断 | 崩溃分析、日志捕获、启动参数与 Java 环境检查 |
| 界面 | 深色 / 浅色主题、无边框窗口、实例封面与加载器图标 |

### 整合包导出

导出流程按 PCL 的规则重建：

```text
实例目录
  → 白名单复制到 overrides/
  → jar / zip 计算 SHA1、SHA512、Murmur2
  → Modrinth / CurseForge 反查下载地址
  → 命中文件写入 modrinth.index.json
  → 删除 overrides 中的本地副本
```

结果是：能识别的模组、资源包、光影只保留下载条目，压缩包体积接近 PCL；配置、脚本与无法识别的文件才进入 `overrides/`。

导出弹窗为全自绘控件，支持格式、内容范围、导出目录与同名覆盖。

### 陶瓦联机

联机模块只保留陶瓦联机：

- 与 HMCL 使用同一套房间码，双方可以互相加入；
- 一键安装 / 更新、创建房间、加入房间、成员列表；
- 房主在游戏内开放局域网后，自动扫描端口；
- 支持备用直连地址与联机状态恢复。

### 网络与 IPv4

启动器与游戏统一优先 IPv4：

```text
启动器 HTTP   → 强制 IPv4，走同一套代理配置
游戏 JVM      → -Djava.net.preferIPv4Stack=true
```

在 IPv6 出口不稳定的网络里，避免登录、皮肤与资源请求长时间等待。

---

## 安装与运行

从 [Releases](https://gitee.com/mobai-33460/aurora-launcher/releases) 下载 `AuroraLauncher-2.4.1.exe`。

运行要求：

```text
Windows 10 / 11 x64
.NET 6 Desktop Runtime
WebView2 Runtime
```

发布包是框架依赖单文件，不内置 .NET 运行时。

首次启动会在 exe 同级创建数据目录；也可以在设置中切换游戏目录。

---

## 工程结构

```text
aurora-launcher/
├─ src/
│  ├─ Core/         IPC、路径、日志、进度、崩溃分析
│  ├─ Accounts/     微软 / 第三方 / 离线账户、皮肤
│  ├─ Download/     Modrinth、CurseForge、多源下载
│  ├─ Instances/    实例、加载器、Java、整合包
│  ├─ Network/      HTTP、服务器 Ping、陶瓦联机
│  ├─ System/       系统信息、内存优化、更新、新闻
│  └─ Launch/       启动参数、版本链、进程与日志
├─ renderer/        React 界面资源
├─ web/             WebView2 桥接脚本
├─ lib/             WebView2 SDK
├─ assets/          静态资源
├─ docs/            文档与更新日志
└─ release/         发布产物
```

`renderer/` 与 `web/aurora-shim.js` 会打包为 `assets.zip`，作为嵌入资源随 exe 分发，首次运行时解压到数据目录。

---

## 关键实现

### 版本链

启动前合并客户端版本、加载器 profile 与继承链，生成最终 classpath、natives 与参数表；同名依赖去重并保留更高版本。

### 下载调度

```text
小文件        单连接
4 MB 以上     1 MB 分段
64 MB 以上    最多 640 段
全局并发      最多 64
单文件并发    最多 12
```

8 秒内低于 50 KB/s 的慢段会二分拆出剩余区间，已下载部分不重复写入。

### 账户

Microsoft 账户使用 OAuth 设备代码登录，令牌刷新只替换 `accessToken` / `refreshToken`，保留账户 `id`、`uuid` 与用户名。

启动前校验令牌：为空或为 `0` 时刷新；仍无效则阻止启动，避免游戏以离线占位令牌进入世界。

### 崩溃分析

崩溃发生时立即分析一次，结果写入：

```text
.aurora/logs/crash-<实例ID>.json
```

「上次崩溃分析」优先读取这份结果，保证弹窗与详情页结论一致；新的 `latest.log` 出现后缓存自动失效。

### 进程

关闭启动器不会结束游戏。启动成功后：

- 游戏进程设为「高」优先级；
- 为对应 `java.exe` 写入高性能 GPU 偏好；
- 同时捕获 stdout / stderr 与 `logs/latest.log`，进程退出后停止监听。

---

## 数据布局

```text
<exe目录>/
├─ .aurora/
│  ├─ settings.json
│  ├─ accounts.json
│  ├─ instances/
│  ├─ logs/
│  ├─ cache/
│  ├─ packs/
│  └─ app/
└─ .minecraft/
   ├─ versions/
   ├─ libraries/
   ├─ assets/
   ├─ runtime/
   └─ saves/
```

账户、配置、日志与游戏数据分离；迁移启动器目录时可以整体带走 `.aurora/`。

---

## 构建

环境：

```text
.NET 6 SDK
Windows x64
```

编译：

```powershell
dotnet build -c Release -p:UseSharedCompilation=false
```

发布单文件：

```powershell
dotnet publish -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o single
```

重新打包界面资源：

```text
renderer/
web/aurora-shim.js
→ assets.zip
```

`assets.zip` 作为嵌入资源写入 exe。

---

## 兼容性

| 项目 | 状态 |
| --- | --- |
| 原版 1.7.10 – 最新正式版 | 支持 |
| Fabric / Quilt | 支持 |
| Forge 1.16.5 | 完整安装与启动实机验证 |
| Forge 其他版本 | 持续验证 |
| NeoForge | 支持，版本矩阵持续验证 |
| OptiFine | 独立版本或放入 `mods` |
| Windows x64 | 主要目标平台 |

---

## 致谢

本项目依赖以下镜像、接口与开放资源：

- **BMCLAPI** — 镜像源与 Forge 安装工具  
  <https://bmclapi.bangbang93.com/>
- **MC 百科** — 模组中文名与资料  
  <https://www.mcmod.cn/>
- **MCIM** — 社区资源镜像  
  <https://www.mcimirror.top/>
- **Modrinth** — 模组、整合包、资源包、光影数据  
  <https://modrinth.com/>
- **CurseForge** — 模组与整合包数据  
  <https://www.curseforge.com/>
- **Terracotta** — 陶瓦联机协议与二进制  
  <https://github.com/burningtnt/Terracotta>
- **PCL / PCL-CE** — Java 选择、账户语义、安装与导出行为参考  
  <https://github.com/Meloong-Git/PCL>

感谢以上项目与服务的作者和贡献者。

---

## 许可证

**GNU Lesser General Public License v3.0 only**（LGPL-3.0-only）。

完整文本见 [LICENSE](LICENSE)。

---

## 免责声明

本项目与 Mojang Studios、Microsoft 无隶属关系。

Minecraft 是 Mojang Studios 的商标。
