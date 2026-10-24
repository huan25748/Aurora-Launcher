# Aurora Launcher · 极光启动器

基于 **Electron + React + TypeScript** 实现的 Minecraft 启动器。启动链路（版本安装、加载器、资源下载、账户、Java、启动参数、崩溃分析和更新检查）由本项目独立实现。

> 当前版本：**1.0.69**

---

## 下载与发布形式

| 形式 | 路径 | 说明 |
| --- | --- | --- |
| **单文件版（推荐）** | `release/AuroraLauncher.exe` | 约 101 MB。双击后解压到 `%LOCALAPPDATA%\AuroraLauncher\runtime\<版本-构建号>` 并运行；该目录不可写时回退到 exe 同级或用户目录。 |
| 便携文件夹 | `release/AuroraLauncher/AuroraLauncher.exe` | 约 231 MB。整个文件夹必须一起复制，不能只拿 exe。 |

上一版本保留为 `release/AuroraLauncher-<版本>.exe`，可用于回退。

单文件版没有代码签名，Windows SmartScreen 可能提示“已保护你的电脑”，选择“更多信息 → 仍要运行”；部分杀毒软件也可能误报自解压程序。

---

## 数据目录与隔离

每个启动器副本使用自己的配置和游戏目录：

- 配置、账户、窗口状态、启动日志保存在启动器旁的 **`.aurora`** 文件夹
- 首次启动在启动器旁创建 **`.minecraft`**，并补齐 `versions`、`libraries`、`assets`、`runtime`、`mods`、`saves`、`resourcepacks`、`shaderpacks`、`config`、`logs` 和 `launcher_profiles.json`
- 因此把 exe 复制到新文件夹后，打开就是空白实例列表，不会继承旧配置
- 如果启动器目录不可写，会退回到带目录哈希的 AppData 子目录

已有游戏目录可以在 **设置 → 游戏 → 存储位置 → 切换游戏文件夹** 切换。切换后会自动扫描目标目录的 `versions/`，已有资源、依赖库和运行时会被复用。

实例列表为空时会自动扫描当前游戏目录的 `versions/`：

- **PCL 版本隔离**：`versions/<版本>/` 内已有 `mods`、`saves`、`resourcepacks`、`options.txt` 时，实例直接指向该版本目录
- **根目录游戏数据**：游戏数据在 `.minecraft` 根目录时，实例指向根目录
- 也可以在实例页点击「导入 .minecraft 版本」再次扫描
- **删除导入实例会删除其对应的游戏文件夹**；`.minecraft` 根目录和 `libraries`、`assets`、`runtime` 等共享引擎目录不会被删除

---

## 功能

### 版本与加载器

- 原版 Minecraft 全版本安装，支持最新的 `26.x` 版本格式
- 新建实例时可选择原版、Fabric、Quilt、Forge、NeoForge，并自定义实例名称
- 新实例创建在 `.minecraft/versions/<实例名>/`，内含合并后的完整版本 JSON，不依赖原版/加载器文件夹
- 安装完成后清理本次产生的临时版本目录，`versions/` 只保留实例自己的目录
- Forge / NeoForge 使用官方安装器（`--installClient`）：
  - 先从 installer 的 `install_profile.json` 和 `version.json` 解析依赖，镜像预下载
  - 自动模式优先使用 BMCLAPI 镜像，官方模式官方优先
  - 安装器 90 秒无输出会自动停止并换源重试，总超时 8 分钟
  - 已安装的加载器版本直接复用，不重复运行安装器
- Fabric / Quilt 直接读取官方 meta 生成 profile
- 支持继承链合并、OS/特性规则求值、natives 解析、旧版资源 `virtual` / `map_to_resources`
- 支持 `default-user-jvm` 参数（1.21.2+ / 26.x 的 ZGC、紧凑对象头等）
- Quick Play 自动进服/进存档、窗口尺寸、全屏、内存、JVM 参数和游戏参数

### 下载与整合包

- 原版、Forge、NeoForge、Fabric、Quilt 可以分别设置下载源，支持「跟随默认 / 自动 / 镜像 / 官方」
- 客户端、依赖库、日志配置并行下载
- 大文件使用可调 Range 分段下载（设置 1–2048 全局并发，单文件自动最多 16 段），合并前校验 SHA1；CDN 慢时回退单流
- CurseForge 文件下载支持从文件 ID 推导 CDN 地址，作者关闭三方下载时仍可安装
- 下载中心内置 Modrinth 与 CurseForge；CurseForge 通过 MCIM 代理，Modrinth 支持官方直连与 MCIM 镜像
  - Modrinth 下载源默认镜像（MCIM），可在设置中切换自动/镜像/官方；自动档优先官方，失败回退 MCIM
  - 模组、资源包、光影、数据包、整合包
  - 搜索、项目详情、文件版本选择、分类筛选
  - 自动安装必需前置依赖
- **本地 Modrinth 整合包导入**：支持 `.mrpack` 和扩展名为 `.zip` 的 Modrinth 包，自动安装对应加载器、下载模组并应用 overrides
- 安装到实例或指定文件夹：
  - 安装到文件夹时先选「游戏版本」，再选该游戏版本对应的「模组版本」
  - 只下载当前选择的文件，不自动补前置
- Modrinth/CurseForge/本地整合包安装完成后清理临时版本目录
- CurseForge 整合包按项目类别把资源包/光影放入正确目录，避免误放进 mods
- 启动前检测重复模组并自动禁用较旧副本，修复重复 mod 导致的绿屏/卡死
- 大型整合包自动提高实例内存，减少启动黑屏卡死
- CurseForge 整合包安装完成后，弹窗列出未能安装的模组名称和文件 ID，并继续完成其余文件安装
- 支持导出整合包：导出 `mods`、`config`、`resourcepacks`、`shaderpacks` 等与 manifest，不包含存档、日志和游戏本体

### 实例与内容

- 多实例隔离，实例页使用紧凑列表布局
- 实例详情包含概览、模组、资源包、光影、存档、截图、服务器、日志、设置
- 模组、资源包、光影启停、删除、导入、打开目录
- 存档解析/备份，截图预览，服务器列表读写与 Ping
- 模组元数据解析：Fabric、Quilt、Forge、NeoForge、旧版 `mcmod.info`
- 模组图标从 jar 内解析并以 data URL 传给界面，不依赖缓存文件协议
- PCL 整合包加载器识别：优先 `PCL/Setup.ini`，其次 FML 参数和加载器库，最后回退文件夹名
- 支持整合包自带封面：`PCL/Logo.png`、`icon.png`、`logo.png`、`pack.png`

### 中文名与搜索

- 内置约 9500 条 MC 百科模组名称索引
- 搜索结果返回时直接附带中文名，列表首帧显示「英文名（中文名）」
- 支持中文名搜索
- Modrinth / CurseForge 搜索结果可附带 MCIM 中文简介，中文查询会自动翻译为英文

### 账户

- 离线账户（标准 OfflinePlayer UUID）
- 微软正版登录：OAuth 设备代码流，令牌自动刷新
- authlib-injector 第三方验证登录
- 账户头像优先使用 3D 头部渲染，依次尝试 Mineatar、Visage、Crafatar、Mineskin、MC-Heads
- 3D 失败时使用高清 2D 头像；全部失败显示用户名首字母渐变头像
- 不再使用旧版“皮肤原图裁 8×8”的 2D 兜底

### Java

- 自动扫描系统 Java 与 `.minecraft/runtime` 中的 Mojang 运行时
- 一键下载 Java 8 / 16 / 17 / 21 / 25
- 按游戏版本自动匹配 Java 主版本

### 界面

- 无边框窗口、自绘标题栏、深色/浅色/跟随系统主题、6 套强调色
- 中性扁平风格：轻微背景色环境光、低阴影、细边框，不使用大面积旋转极光动画
- 首页：当前实例、账户、统计、最近游玩、资讯
- 控制台默认完全收起，点击右上角状态按钮展开，再点收起
- 首次启动静默创建 `.minecraft`，不弹出欢迎/设置引导
- 实例卡片图标 42px，封面高度 70px
- 设置页包含常规、游戏、下载、Java、高级、存储
- 存储页：统计总占用、缓存、整合包临时文件、安装器残留、依赖库和资源占用
- **首次启动切换中文**：实例第一次通过 Aurora 启动时写入 `options.txt` 的 `lang:zh_cn`，之后不再覆盖用户手动修改
- 应用图标统一用于标题栏、窗口、任务栏、任务管理器、内外层 exe

### 更新检查

- 使用 GitHub Releases：
  `https://api.github.com/repos/huan25748/Aurora-Launcher/releases/latest`
- 启动后约 4 秒自动检查（可在 设置 → 高级 → 关于版本 关闭）
- 有更新才弹出「发现新版本」；没有更新或请求失败时静默
- 手动「检查更新」：有更新弹窗，已是最新只显示提示，失败显示错误
- 更新弹窗包含版本号、Release 标题、更新说明和「前往下载」
- 只检查并打开 GitHub Release，不自动下载替换 exe

### 崩溃分析与诊断

- 自动分析崩溃报告和 `latest.log`
- 也会读取启动器捕获的 `logs/aurora-*.log`，覆盖 JVM 尚未创建 `latest.log` 的启动失败
- 识别内存不足、Java 版本不匹配、重复模组、缺少前置、Mixin 冲突、图形驱动异常、文件损坏、端口占用等
- 识别 `Could not create the Java Virtual Machine` / `A fatal exception has occurred`，提示下载正确版本的 Java
- 提供关闭硬件加速、启动日志、单文件解压日志等诊断手段

### 存储管理

设置 → 存储：

- 统计 `assets`、`libraries`、`versions`、`instances`、缓存、整合包缓存、natives 和安装器残留
- 「清理缓存与安装器」：删除缓存、整合包临时文件、natives、日志和旧安装器 jar/log，不删除游戏版本、依赖库或资源
- 「精简未使用依赖库与资源」：扫描所有已安装版本 JSON，只删除没有被任何版本引用的 `libraries` 和 `assets` 文件
- 安装 Forge/NeoForge、Modrinth/CurseForge 整合包后自动清理安装器和临时压缩包

---

## 单文件版启动流程

`scripts/sfx/Stub.cs` 编译为 C# 引导程序，exe 尾部拼接 ZIP 负载：

```text
[stub][zip][int64 长度]["AURORASF"]
```

首次启动直接从 exe 尾部流式读取 ZIP，解压到 `%LOCALAPPDATA%\AuroraLauncher\runtime\<版本-构建号>`，不向 `%TEMP%` 写临时 zip。每个构建使用独立运行时目录。

启动失败时可以检查：

- `<启动器目录>/.aurora/startup.log`
- `%LOCALAPPDATA%\AuroraLauncher\sfx.log`
- exe 旁新建空文件 `disable-gpu.txt` 强制软件渲染
- 确认杀毒软件没有隔离解压目录或 exe

---

## 开发与构建

环境：Node.js ≥ 20，Windows 10/11。

```bash
npm install
npm run dev          # Vite + esbuild watch + Electron
npm run build        # Vite 生产构建
npm start            # 构建并启动
npm run build:local  # 受限环境备用（直接调用 esbuild CLI）
```

打包：

```bash
npm run package          # 便携文件夹 release/AuroraLauncher/
npm run package:single   # 单文件 release/AuroraLauncher.exe
```

UI 预览（无需窗口）：

```bash
npm run preview   # 生成 dist/preview/index.html（mock IPC）
```

---

## 测试

```bash
npm run typecheck
npm run test:all       # Node 端到端 + 服务层
npm run test:render    # jsdom 页面渲染
npm run test:portable  # 启动器文件夹配置与 .minecraft 独立性
node scripts/test/run-all.mjs java     # Java 下载
node scripts/test/run-all.mjs assets   # 现代/旧版资源
node scripts/test/run-all.mjs forge    # Forge 安装器（慢）
```

测试会下载真实文件到 `.e2e-home/`，占用约 1 GB 以上。

发布前至少执行：

```bash
npm run typecheck
npm run build:local
npm run test:render
npm run package:single
```

---

## 已知限制

- 微软登录默认使用公开 OAuth Client ID，可在「设置 → 高级」替换
- Forge / NeoForge 依赖官方安装器，网络不佳时仍可能需要重试
- 单文件版自解压行为可能被杀毒软件误报
- 更新检查只打开 GitHub Release，不会自动替换当前 exe
- CurseForge 由第三方 API 代理提供，单次查询存在数量上限
- OptiFine 需要使用官方安装器生成版本后再导入
- 没有代码签名
- `src/main/core/mcmod-names.json` 来自 MC 百科数据，再分发前请确认数据许可
- `resources/rcedit-x64.exe` 是打包用第三方工具，再分发时请保留其许可说明

---

## 许可与致谢

MIT License。本项目与 Mojang Studios、Microsoft 无隶属关系；Minecraft 是 Mojang Studios 的商标。

数据与镜像来源：

- BMCLAPI / bangbang93
- MC 百科
- MCIM / z0z0r4
- Modrinth
- CurseForge
- MyMemory
- Crafatar / MC-Heads
- Fabric、Quilt、Forge、NeoForge 官方元数据

感谢这些项目和服务为启动器提供元数据、镜像与头像支持。
