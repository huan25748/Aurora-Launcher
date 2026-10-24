# Aurora Launcher · 极光启动器

基于 **Electron + React + TypeScript** 从零实现的现代 Minecraft 启动器。界面完全重新设计，核心启动链路（版本安装、加载器、资源下载、账户、Java、启动参数、崩溃分析）独立实现。

> 分析 `PCL-CE-dev` 的目的是理解启动器的完整能力边界与 Mojang / 加载器生态的接口约定；Aurora 以自己的架构重新实现这些能力，未复制其代码。

---

## 两种发布形式

| 形式 | 路径 | 说明 |
| --- | --- | --- |
| **单文件版（推荐）** | `release/AuroraLauncher.exe` | 约 100 MB（1.0.22 已精简语言包与渲染层）。双击后自动解压运行文件到 `%LOCALAPPDATA%\AuroraLauncher\runtime\<版本-构建号>`（首次约 5 秒），之后秒开；如果该目录不可写，会依次回退到 exe 同级 `AuroraLauncher-runtime` 和用户目录。 |
| 便携文件夹 | `release/AuroraLauncher/AuroraLauncher.exe` | 约 272 MB。整个文件夹必须一起复制，**不能只拿 exe**。 |

单文件版可放在任意目录。它会**优先读取已有 `.minecraft`**（见下文），不会另建一套游戏目录。

> 由于没有代码签名，Windows SmartScreen 可能提示“已保护你的电脑”，选择“更多信息 → 仍要运行”；部分杀毒软件也可能误报自解压程序。

---

## 每个启动器文件夹独立

启动器**不再自动读取** `%APPDATA%\.minecraft`、`D:\Minecraft` 或其他启动器的数据。复制到新文件夹后第一次打开就是**空白实例列表**，配置也只属于当前文件夹。

**每次启动都会在启动器所在文件夹创建 `.minecraft`**，并补齐 `versions`、`libraries`、`assets`、`runtime`、`mods`、`saves`、`resourcepacks`、`shaderpacks`、`config`、`logs`、`launcher_profiles.json` 等运行所需目录/文件。新实例默认安装到这个 `.minecraft`。

如果需要使用已有的游戏文件夹：

- 打开「设置 → 存储位置 → 切换游戏文件夹」，选择已有 `.minecraft`
- 或点击「启动器文件夹」回到当前启动器自己的 `.minecraft`
- 切换后会自动扫描所选目录的 `versions/` 并导入版本，旧文件不会被修改

首次启动且实例列表为空时，会自动扫描当前游戏目录的 `versions/` 并把已有版本导入为实例：

- **PCL 版本隔离**：如果 `versions/<版本>/` 里已有 `mods`、`saves`、`resourcepacks`、`options.txt` 等，实例直接指向该版本目录，原模组与存档立即可用。
- 如果游戏数据在 `.minecraft` 根目录（官方启动器方式），实例指向根目录。
- 导入的实例标记为「导入」，删除时只从启动器列表移除，**不会删除原始游戏文件**。
- 也可以在「实例」页点击「导入 .minecraft 版本」手动再次扫描。

已有 `assets`、`libraries`、`runtime` 会被直接复用；只补缺失的客户端 jar / 资源。

> 启动器配置与账户令牌保存在**启动器同级的 `.aurora` 文件夹**（`config.json`、`accounts.json`、`startup.log`），因此每个启动器副本互不影响；如果启动器目录不可写，才使用按目录哈希隔离的 AppData 子目录。

---

## 功能

### Forge / NeoForge 安装说明

- 使用 **Forge 官方安装器**（`--installClient`）安装，这是 1.13+ 唯一可靠的方式：安装器会下载 Forge 运行库并执行补丁处理器
- 采用与 PCL 相同的加速思路：**先从 installer 的 `install_profile.json` + `version.json` 解析全部库和处理器 classpath，再并行预下载（含 asm 9.2/9.6、srgutils 0.4.3/0.4.9/0.5.6 等同一构件的多个版本），最后才运行安装器**
- 预下载使用 BMCLAPI 镜像（`https://bmclapi2.bangbang93.com/maven/`），并额外下载 Mojang mappings；安装器阶段实测 **0 次串行库下载**
- **只安装运行所需内容**：版本目录里只有一个 JSON（实测 21 KB），Forge 运行库放在共享的 `libraries/` 中（Forge 相关约 11 MB；总 libraries 136 MB 主要是原版共享库）
- 安装加载器时**不会下载资源文件（assets）**，资源会在启动游戏时按需补齐
- **Forge 1.20+ 模块冲突修复**：`BootstrapLauncher` 已把补丁客户端作为 `minecraft` 模块加载，启动器不再把继承的原版 `1.20.1.jar` 加入 classpath（否则报 `ResolutionException: Module minecraft contains package net.minecraft.server`）；PCL 那种自带完整 jar 的导入实例仍保留自己的 jar
- 安装器只把阶段日志（Task / Building Processors / 错误）转发到界面；Forge 会输出数万行类级日志，全部转发会明显拖慢安装
- **已安装的 Forge/NeoForge 版本会直接复用**，不会重复运行安装器（实测第二次 0 秒）
- 实测 Forge 1.20.1-47.4.28 冷安装：**188 秒 → 106 秒**（其中预下载 11 秒、官方处理器约 88 秒）。剩余时间主要是 Forge 自身的 `jarsplitter`（约 30 秒）、`ForgeAutoRenamingTool`（约 16 秒）和 `binarypatcher`（约 9 秒），PCL 也会执行同样的处理器；旧版 Forge（1.12.2 等）没有这些现代处理器，通常接近 1 分钟

### 启动与版本
- 原版 Minecraft 全版本安装，支持最新的 `26.x` 版本格式
- 安装原版时可**同时选择 Fabric / Quilt / Forge / NeoForge**，并自定义实例名称；名称重复会提示改名
- **`default-user-jvm` 参数支持**（1.21.2+ / 26.x 的 ZGC、紧凑对象头等默认参数，含 Windows 版本规则）
- Fabric / Quilt / Forge / NeoForge 一键安装（Forge/NeoForge 官方安装器 + 自动重试）
- 多实例隔离；**新实例直接创建在 `.minecraft/versions/<实例名>/`**，内含**合并后的完整版本 JSON（不再依赖原版/加载器文件夹）**、mods/saves 和必要时的客户端 jar；安装完成后会清理本次新建的原版/加载器临时版本目录，因此 `versions/` 里只保留你命名的实例文件夹（删除实例会删除该目录，导入实例除外）
- 下载中心版本列表不再显示“已安装”，所有版本统一显示「新建实例」；原版/加载器安装都在创建实例时按需完成
- 导入实例复用 PCL/官方目录，不修改原文件
- 继承链合并、OS / 特性规则求值、natives 解析、旧版资源 `virtual` / `map_to_resources`
- Quick Play 自动进服 / 进存档、窗口尺寸、全屏、内存、JVM/游戏参数
- Java 自动扫描与匹配（含 `.minecraft/runtime` 中 Mojang 运行时），一键下载 Java 8/16/17/21/25
- 启动控制台：任务进度、实时日志、停止游戏、崩溃原因分析

### 账户
- 离线账户（标准 OfflinePlayer UUID）
- 微软正版登录：OAuth 设备代码流，令牌自动刷新
- authlib-injector 第三方验证登录

### 内容
- 内置 Modrinth：模组、资源包、光影、数据包、整合包
- **默认搜索全部资源**（不再自动套用实例版本/加载器筛选，避免出现“只能看到 100 个”）；支持版本 / 加载器筛选、`兼容当前实例` 一键筛选与分页加载
- 自动安装必需前置依赖
- `.mrpack` 整合包安装，自动补齐加载器
- 模组元数据解析（Fabric / Quilt / Forge / NeoForge / 旧版 mcmod.info）
- 启停、删除、导入、打开目录；存档解析 / 备份；截图预览；服务器列表读写与 Ping
- PCL 整合包加载器识别：优先读取 `PCL/Setup.ini`，其次读取 FML 参数 / Forge / NeoForge / Fabric / Quilt 库，再回退文件夹名；已导入的旧实例启动时自动修正
- 自定义文件夹名的整合包（如「剑与王国」）也能正确识别 Forge 版本和原版版本号

### 界面
- 极光渐变 + 玻璃拟态，深/浅主题、6 套强调色
- 无边框窗口、自绘标题栏、首页英雄卡片、实例九标签详情
- 首次启动静默创建 `.minecraft` 所需目录，不再弹出欢迎/设置引导弹窗
- 实例页改为**列表布局**，加载器图标统一为纯黑磁贴；首页实例标题与切换下拉框使用**无背景加载器抠图**（不使用整合包封面），原版使用高分辨率立体草方块
- 支持整合包自带封面：自动读取 `PCL/Logo.png`、`icon.png`、`logo.png`、`pack.png` 并作为实例图标
- Forge / NeoForge 版本列表接入官方 Maven 源，自动模式官方优先、镜像回退；下载源下拉（自动官方优先 / 镜像 / 官方），新增「检测连通性」；游戏版本清单/版本 JSON 默认官方优先，BMCLAPI 仅作回退，避免镜像超时导致游戏下载失败
- Modrinth 版本列表优先走官方 API，避免 JEI 等版本数很大的项目在镜像端超时导致“没有兼容版本”
- 修复 26.1+ NeoForge 启动时 logging 配置缺少 id 导致的 path undefined 崩溃
- 下载游戏时客户端、依赖库、日志配置并行下载；Forge/NeoForge 的原版基础文件与安装器也同时下载
- 大文件下载使用可调 Range 分段下载（设置中 1–2048 全局并发，单文件自动最多 16 段）；客户端下载显示实时字节进度，合并前校验 SHA1；官方 CDN 慢或限速时自动回退单流镜像，并保留实时字节进度
- 修复 2D 头像重试导致的 accounts 循环刷新，解决未响应/卡顿
- 账户头像优先 3D 渲染并自动重试；强调色文字跟随所选强调色；安装弹窗点击后立即关闭；修复安装器完成进度条、Modrinth 依赖进度不结束
- CurseForge 搜索新增分类筛选；说明 CurseForge API 单次查询最多 1 万条
- 恢复侧边栏分组、保留轻微背景光效、缩小实例图标
- 界面改为中性扁平风格，减少渐变、光效与装饰元素
- 新增 GitHub Releases 检查更新与自动检查开关
- 控制台默认完全收起，由右上角状态按钮展开；状态按钮移到右侧
- 崩溃分析识别 JVM 启动失败并提示下载正确 Java
- 新增首次启动自动切换游戏语言为简体中文
- Modrinth/CurseForge/本地整合包安装后清理临时版本目录
- Forge/NeoForge 安装器默认走镜像、90 秒无输出自动切换、安装器超时保护
- 支持直接导入本地 Modrinth 整合包（.mrpack / .zip）
- 新增存储统计与缓存/未使用库清理；安装器与整合包临时文件自动删除
- 删除导入实例时清理整个游戏文件夹（含外部导入目录）
- 重绘应用图标，统一窗口、任务栏、任务管理器与打包 exe 图标
- 文件夹安装先选游戏版本再选模组版本；删除导入实例时一并删除其文件夹
- 模组图标改走 data URL；返回保留下载页标签；新增头像源与文件夹游戏版本弹窗；移除模组加载器鸣谢；账号头像删除 2D 皮肤裁剪兜底
- 下载设置支持按原版、Forge、NeoForge、Fabric、Quilt 分别选择下载源
- 模组中文名在搜索结果返回时直接附带，列表首帧即显示；内置 9500 条名称索引
- 模组列表显示 MC百科中文名，中文名可直接搜索
- 模组列表显示 MCIM 中文简介，支持中文搜索（自动翻译查询）
- 模组支持选择指定文件夹安装；安装前置前检查 mods 目录已有模组
- 下载中心新增 CurseForge 来源：搜索、详情、文件版本、模组安装、整合包安装（通过 MCIM 代理读取 CurseForge API）
- 新增「导出整合包」：导出 mods/config/resourcepacks/shaderpacks 等与 manifest，不包含存档、日志和游戏本体
- 账户页读取正版皮肤，显示完整 3D 头部头像（http 纹理自动升级为 https）
- 性能：渲染层 minify 并移除 sourcemap，只保留中英文语言包，账户头像迁移放到后台，减少大面积 backdrop-filter 重绘
- 正版头像改为完整 3D 头部渲染（mc-heads 头像）+ 本地裁剪兜底，不再只显示眼睛局部
- 原版实例图标统一使用草方块，不再替换成玩家皮肤
- 登录/刷新时在主进程获取/缓存玩家完整头部头像（data URL），已有账户首次读取时自动迁移
- 下载中心、账户、设置、关于
- 任务控制台、Toast、Portal 下拉菜单
- 中英文外壳

---

## 启动打不开时会做什么

启动器内置了多层自保与诊断：

- 窗口不再只依赖 `ready-to-show`：3 秒兜底显示、`did-finish-load` 显示、加载失败弹窗
- 上次启动未完成渲染时，下次自动**关闭硬件加速**重试（也可在 exe 旁创建 `disable-gpu.txt` 强制软件渲染）
- 保存的窗口坐标若不在任何屏幕上，自动居中
- 预加载脚本 / 页面文件缺失时，界面显示明确的错误页而不是纯黑
- 渲染进程崩溃、主进程未捕获异常都会写入日志并弹窗
- 启动日志：`%APPDATA%\AuroraLauncher\startup.log`
- 单文件解压日志：`%LOCALAPPDATA%\AuroraLauncher\sfx.log`

如果仍然打不开：

1. 确认系统为 Windows 10/11 x64；
2. **确认运行的是新版本**：每个构建的运行时目录名都带独立构建号（如 `1.0.22-mvkmfxpd`），不会复用旧的 `1.0.0-*` 目录；如果启动时完全看不到解压窗口，先删除旧的 `%LOCALAPPDATA%\AuroraLauncher\runtime` 再试；
3. 查看 `startup.log` / `sfx.log`；
4. 在 exe 旁新建空文件 `disable-gpu.txt` 后重试；
5. 确认杀毒软件没有隔离解压目录或 exe（必要时把启动器目录加入白名单）；
6. 尝试便携文件夹版本（`release/AuroraLauncher/`）。

---

## 开发与构建

环境：Node.js ≥ 20，Windows 10/11。

```bash
npm install
npm run dev          # Vite + esbuild watch + Electron
npm run build        # Vite 生产构建
npm start            # 构建并启动
npm run build:local  # 受限环境备用（直接用 esbuild CLI）
```

打包：

```bash
npm run package          # 便携文件夹 release/AuroraLauncher/
npm run package:single   # 单文件 release/AuroraLauncher.exe
```

单文件实现：`scripts/sfx/Stub.cs` 编译为 C# 引导程序，尾部拼接 ZIP 负载
`[stub][zip][int64 长度]["AURORASF"]`；首次启动解压到 `%LOCALAPPDATA%` 并启动内部 Electron 应用。
解压时**直接从 exe 尾部流式读取 ZIP**，不再向 `%TEMP%` 写临时 zip，避免杀毒软件/权限策略导致的“访问被拒绝”。

UI 预览（无需窗口）：

```bash
npm run preview   # 生成 dist/preview/index.html（mock IPC）
```

---

## 测试

```bash
npm run typecheck
npm run test:all       # Node 端到端 + 服务层
npm run test:render    # jsdom 八个页面渲染（含项目详情）
npm run test:portable  # 每个启动器文件夹的配置/.minecraft 独立性
node scripts/test/run-all.mjs java     # Java 下载
node scripts/test/run-all.mjs assets   # 现代/旧版资源
node scripts/test/run-all.mjs forge    # Forge 安装器（慢）
```

已实际通过：

| 套件 | 结果 |
| --- | --- |
| TypeScript | 通过 |
| Node 端到端（含 .minecraft 布局 / versions 自包含实例 / 临时目录清理 / Forge 模块 classpath） | **52/52** |
| 服务层（含 Modrinth 全量搜索 / 分页 / 项目字段） | **19/19** |
| jsdom 渲染（含项目详情页） | **8/8** |
| **26.3 / `.minecraft` 读取** | **21/21**（Java 25、default-user-jvm → ZGC、无 G1 冲突、导入目录、GPU 安全模式） |
| Java 运行时下载 | 通过 |
| 现代/旧版资源 | 通过 |
| Forge 1.20.1-47.4.28 | 通过（PCL 式预下载，冷安装 **106s**，安装器 0 次串行下载） |
| Forge 1.20.1 实际启动 | 通过（修复模块冲突后实测进入主菜单） |
| Forge 重复安装复用 | **6/6**（0 秒复用，不下载资源） |
| 单文件无临时 zip 解压 | 通过（流式读取尾部 ZIP） |
| 启动器独立配置 / 本地 .minecraft | **6/6** |

> 测试会下载真实文件到 `.e2e-home/`（约 1.2 GB）。

---

## 已知限制

- CurseForge 未接入（需要 API Key）；OptiFine 需用官方安装器生成版本后导入。
- 微软登录默认使用公开 OAuth Client ID，可在「设置 → 高级」替换。
- Forge/NeoForge 依赖官方安装器，网络不佳时可能需重试。
- 单文件版因自解压行为可能被杀毒软件误报。
- 暂无自动更新、皮肤编辑。

---

## 许可

MIT。本项目与 Mojang Studios、Microsoft 无隶属关系；Minecraft 是 Mojang Studios 的商标。
