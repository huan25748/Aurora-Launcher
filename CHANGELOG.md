# 更新日志

当前版本：**1.1.8**

## 下载

- GitHub Releases：在仓库的 Releases 页面下载最新 `AuroraLauncher-1.1.8.exe`
- 蓝奏云：<https://mobaijianhe.lanzouc.com/b01euviyte>

历史版本继续保留在 `release/` 目录中，可直接回退。

---

## 1.1.8

- 下载页新增“愚人节”版本分类。
- 愚人节版本识别规则改为：

```text
已知历史愚人节版本 ID
或
type = snapshot 且 releaseTime 包含 -04-01T
```

- 今年的 `26w14a` 已归入愚人节分类。
- 4 月 1 日发布的正式版不会再被误归入愚人节。

## 1.1.7

- 愚人节分类增加按 4 月 1 日发布时间自动识别。
- 修复今年 `26w14a` 未显示的问题。

## 1.1.6

- 关于页 Light-Beacon 头像改为本地图片，不再依赖 GitHub 网络。
- 每次启动启动器都会扫描 `versions` 目录，不再要求实例列表为空。
- 已导入实例会自动修正错误的加载器类型。
- NeoForge 的 `NeoForge_` 目录命名可正确识别为 `neoforge`。
- 下载页新增愚人节、远古版本分类。

## 1.1.5

- 修复 NeoForge 导入实例被识别为原版的问题。
- 识别顺序改为 NeoForge → Forge → Fabric / Quilt → 原版。
- 版本 JSON 中检查 `net.neoforged`，避免 `bootstraplauncher` 被误判为普通 Forge。

## 1.1.4

- 关于页 Light-Beacon 鸣谢项链接改为 GitHub 仓库。
- 使用 GitHub 头像显示。

## 1.1.3

- 预览版资讯补上封面图。
- 从版本 Markdown 文件头读取 `version-image-link`。

## 1.1.2

- PCL2 新闻主页 API 未返回快照时，从 Mojang 版本清单补齐最新预览版。
- 快照资讯加载对应的完整更新日志。

## 1.1.1

- 修复资讯列表为空的 `The node already has a parent` 异常。
- 版本资讯、Mojang 新闻可正常显示。
- 新增 `news-v3` 资讯缓存。

## 1.1.0

- 迁移到 .NET 6 + WPF + WebView2。
- C# 后端实现下载、安装、Java 管理、账户、启动和实例管理。
- 支持原版、Fabric、Quilt、Forge、NeoForge、OptiFine。
- 支持 Microsoft、Authlib、离线账户。
- 支持 Modrinth、CurseForge、整合包、内容管理。
- 支持资讯站内阅读、崩溃分析、更新检查。
