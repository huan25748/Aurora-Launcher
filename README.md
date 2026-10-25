# Aurora Launcher 1.1.0（WPF / WebView2）

这是 Electron 版 Aurora Launcher 1.0.78 的 .NET WPF 迁移工程。

## 当前阶段

第二阶段（进行中）：可运行的 WPF + WebView2 外壳，并开始迁移真实 C# 后端。

已实现：

- WPF 窗口与 1.1.0 版本号
- 复用 Electron 版打包后的完整 React 界面（`renderer/`）
- WebView2 本地虚拟站点加载，不依赖本地 HTTP 端口
- 启动器目录隔离：
  - 游戏目录：`<exe目录>/.minecraft`
  - 配置目录：`<exe目录>/.aurora`
- 自动创建 `.minecraft` 基础目录
- 扫描 `.minecraft/instances/*/instance.json` 供界面实例列表读取
- C# RPC 桥：前端 `invoke` → WebView2 消息 → `BridgeService` → JSON 响应
- 设置持久化到 `<exe目录>/.aurora/config.json`
- 实例列表/实例详情/实例更新读取真实 `instance.json`
- `app.info` 返回真实目录、实例数、版本数
- C# Java 扫描：runtime、JAVA_HOME、PATH、常见 JDK 安装目录
- C# Java 运行时下载：Mojang all.json、组件清单、SHA1 校验，安装到 `.minecraft/runtime`，支持删除托管 Java
- 实例 CRUD：创建、更新、删除、复制、大小统计、打开目录
- 内容管理：mods / resourcepacks / shaderpacks / datapacks 列表、启停、删除、导入
- 设置：切换游戏文件夹、恢复便携目录、系统信息、存储统计/清理
- 世界与截图读取、世界备份、世界删除
- 连通性检测：Mojang、BMCLAPI、Forge/NeoForge/Fabric/Quilt、Modrinth、OptiFine 节点
- C# 版本清单读取与缓存，支持官方/BMCLAPI 回退
- C# 已安装版本扫描、loader 类型识别、Java 版本要求、版本大小
- C# 离线账户：添加、列表、切换、删除，持久化到 `.aurora/accounts.json`
- C# 原版安装：版本 JSON、client jar、libraries、natives、asset index、assets objects
- 安装完成后创建真实实例并写入 `instances/<id>/instance.json`
- C# 原版 / Fabric / Quilt 启动：版本 chain、classpath、natives 解压、JVM/game 参数替换、Java 选择、离线账户
- 启动状态与游戏日志通过 C# 事件推送到前端
- 已用真实 1.21.1 安装+离线启动验证到主菜单
- 已用真实 Fabric Loader 0.16.9 + 1.21.1 安装并启动到主菜单
- 游戏进程不再重定向 stdout/stderr，改从 `logs/latest.log` 读取日志，避免 Windows 管道导致 Fabric 首次 remap 失败
- 窗口最小化 / 最大化 / 关闭 / 标题消息桥

尚未实现（后续阶段）：

- Microsoft / 离线 / Authlib 账户系统
- Forge、NeoForge、OptiFine 安装（Fabric/Quilt 已实现；Forge/NeoForge 已改为手动解析安装器，待 Forge 服务器恢复后验证）
- 游戏启动已实现原版基础路径；崩溃分析待实现
- Modrinth 搜索 / 项目 / 版本 / 下载安装已实现
- OptiFine 版本列表与直接放入 mods 的下载接口
- 游戏日志读取、日志文件列表、简单崩溃原因分析
- Minecraft 服务器状态 ping：版本、人数、MOTD、延迟、图标
- CurseForge、整合包尚未实现
- 内容管理、世界、服务器、截图
- 存储统计、清理、更新检查
- 与 1.0.78 完全一致的全部后端行为

当前 `web/aurora-shim.js` 中未实现的后端接口仍返回空数据或占位结果。界面可正常浏览，设置和实例读取已接真实 C# 后端，但安装、启动和下载尚未迁移，不能当作完整启动器使用。

## 单文件版

发布产物：

```text
release/AuroraLauncher-1.1.0.exe
```

单文件包含：

- WPF 程序
- React 界面（`assets.zip` 内嵌）
- `aurora-shim.js`
- `WebView2Loader.dll`

首次运行会解压界面资源到：

```text
<exe目录>/.aurora/app
```

运行要求：

- .NET 6 Desktop Runtime
- WebView2 Runtime

说明：单文件版为框架依赖单文件，不包含 .NET 运行时本身。

## 构建

环境要求：

- .NET 6 SDK / Windows Desktop Runtime
- WebView2 Runtime
- `lib/` 下的 WebView2 SDK 程序集

```powershell
dotnet build -c Release -p:UseSharedCompilation=false
dotnet publish -c Release -o release -p:UseSharedCompilation=false
```

运行：

```text
release/AuroraLauncher.exe
```

注意：在当前 DSH 受限沙箱下，`dotnet` 的 C# 编译器需要通过管道输出，必须使用完整访问权限构建；普通终端不受此限制。
