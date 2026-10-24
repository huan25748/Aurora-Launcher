// ---------------------------------------------------------------------------
// Aurora Launcher - shared types between main and renderer
// ---------------------------------------------------------------------------

export type ThemeMode = 'dark' | 'light' | 'system'
export type DownloadSource = 'auto' | 'official' | 'mirror' | 'prefer-official'
export type LoaderKind = 'vanilla' | 'fabric' | 'quilt' | 'forge' | 'neoforge' | 'optifine'
export type ContentKind =
  | 'mods'
  | 'resourcepacks'
  | 'shaderpacks'
  | 'saves'
  | 'screenshots'
  | 'datapacks'

export type TaskStatus = 'pending' | 'running' | 'done' | 'error' | 'skipped' | 'cancelled'

export interface ProgressStep {
  id: string
  title: string
  detail?: string
  current: number
  total: number
  status: TaskStatus
}

export interface ProgressGroup {
  id: string
  title: string
  steps: ProgressStep[]
  status: 'running' | 'done' | 'error' | 'cancelled'
  startedAt: number
  finishedAt?: number
  error?: string
}

export interface ProgressSnapshot {
  groups: ProgressGroup[]
  activeGroupId?: string
}

export interface LogLine {
  ts: number
  source: 'launcher' | 'game' | 'download' | 'auth'
  level: 'info' | 'warn' | 'error' | 'debug'
  text: string
}

export interface DownloadSourceMap {
  vanilla?: DownloadSource
  forge?: DownloadSource
  neoforge?: DownloadSource
  fabric?: DownloadSource
  quilt?: DownloadSource
}

export interface UpdateInfo {
  currentVersion: string
  latestVersion: string
  hasUpdate: boolean
  releaseUrl: string
  title: string
  notes: string
  publishedAt?: string
}

export interface StorageStats {
  root: string
  total: number
  assets: number
  libraries: number
  versions: number
  instances: number
  cache: number
  packs: number
  natives: number
  runtime: number
  mods: number
  installerArtifacts: number
}

export interface Settings {
  rootDir: string
  language: 'zh-CN' | 'en-US'
  theme: ThemeMode
  accent: string
  downloadSource: DownloadSource
  /** Per-loader overrides; omitted entries fall back to downloadSource. */
  downloadSources?: DownloadSourceMap
  maxConcurrency: number
  /** Global concurrent range requests (1-2048). */
  downloadThreads: number
  proxyEnabled: boolean
  proxyHost: string
  proxyPort: number
  minimizeOnLaunch: boolean
  showConsoleOnLaunch: boolean
  keepLauncherOpen: boolean
  closeBehavior: 'exit' | 'tray'
  defaultMemory: number
  autoMemory: boolean
  jvmArgs: string
  gameArgs: string
  javaPath: string
  msClientId: string
  enableStats: boolean
  lastSelectedInstanceId: string
  windowBounds?: { width: number; height: number; x?: number; y?: number }
  onboardingDone: boolean
  checkUpdates: boolean
  downloadAssets: boolean
  downloadNatives: boolean
  useAuthlibForOffline: boolean
  autoImportVersions: boolean
  /** On the first launch of an instance, set the in-game language to Simplified Chinese. */
  firstLaunchChinese: boolean
}

export interface Instance {
  id: string
  name: string
  versionId: string
  /** Original installed version this instance was created from (informational). */
  sourceVersionId?: string
  /** Version JSON is fully merged and does not depend on another versions/ folder. */
  selfContained?: boolean
  mcVersion: string
  loader: LoaderKind
  loaderVersion?: string
  gameDir: string
  icon: string
  /** Absolute path to a pack-provided icon (PCL/Logo.png, icon.png, …). */
  iconPath?: string
  color: string
  javaPath?: string
  memory?: number
  jvmArgs?: string
  gameArgs?: string
  createdAt: number
  lastPlayedAt?: number
  lastLaunchedAt?: number
  totalPlayTimeMs: number
  playCount: number
  notes?: string
  serverToJoin?: string
  width?: number
  height?: number
  fullscreen?: boolean
  quickLaunch?: boolean
  /** First-launch language initialization has already run for this instance. */
  languageInitialized?: boolean
  /** Imported from an existing .minecraft installation; game files are not owned by the launcher. */
  imported?: boolean
}

export interface McVersionSummary {
  id: string
  type: 'release' | 'snapshot' | 'old_beta' | 'old_alpha'
  releaseTime: string
  url: string
  sha1?: string
}

export interface VersionManifest {
  latest: { release: string; snapshot: string }
  versions: McVersionSummary[]
  fetchedAt: number
}

export interface InstalledVersion {
  id: string
  type: string
  releaseTime?: string
  mcVersion?: string
  loader?: LoaderKind
  loaderVersion?: string
  mainClass?: string
  hasJar: boolean
  sizeBytes: number
}

export interface LoaderVersion {
  version: string
  stable: boolean
  mcVersion: string
}

export interface JavaInfo {
  path: string
  version: string
  major: number
  arch: 'x64' | 'x86' | 'arm64' | 'unknown'
  vendor: string
  is64Bit: boolean
  source: string
  managed?: boolean
}

export interface Account {
  id: string
  type: 'offline' | 'microsoft' | 'authlib'
  username: string
  uuid: string
  accessToken: string
  refreshToken?: string
  expiresAt?: number
  skinUrl?: string
  capeUrl?: string
  avatarUrl?: string
  /** head3d = isometric whole-head render; face = 8x8 face crop fallback. */
  avatarMode?: 'head3d' | 'face'
  /** Render source (mineatar/visage/crafatar/mineskin/mcheads or 2D variants). */
  avatarSource?: string
  /** Avatar pipeline version; 2 = new multi-source / 2D-quality fallback. */
  avatarVersion?: number
  /** Last 3D-avatar migration attempt; prevents retry loops for failed faces. */
  avatar3dTriedAt?: number
  authServer?: string
  authServerName?: string
  xuid?: string
  createdAt: number
  lastUsedAt?: number
}

export interface ServerEntry {
  name: string
  ip: string
  icon?: string
  hidden?: boolean
}

export interface ServerStatus {
  online: boolean
  version?: { name: string; protocol: number }
  players?: { online: number; max: number; sample?: { name: string; id: string }[] }
  motd?: { text: string; html?: string }
  ping?: number
  favicon?: string
  error?: string
}

export interface ContentItem {
  name: string
  fileName: string
  path: string
  enabled: boolean
  size: number
  mtime: number
  kind: ContentKind
  meta?: ModMeta
  icon?: string
  /** Data URL so the renderer never depends on custom-protocol cache paths. */
  iconData?: string
}

export interface ModMeta {
  id?: string
  name?: string
  version?: string
  description?: string
  authors?: string[]
  loader?: string
  mcVersions?: string[]
  dependencies?: { id: string; version?: string; optional?: boolean }[]
  fileName?: string
  icon?: string
}

export interface WorldItem {
  name: string
  folderName: string
  path: string
  size: number
  mtime: number
  lastPlayed?: number
  gameVersion?: string
  icon?: string
  hardcore?: boolean
  cheats?: boolean
}

export interface ScreenshotItem {
  fileName: string
  path: string
  size: number
  mtime: number
}

export interface ModrinthHit {
  project_id: string
  slug: string
  title: string
  description: string
  categories: string[]
  display_categories?: string[]
  versions: string[]
  downloads: number
  follows: number
  icon_url?: string
  author: string
  project_type: string
  date_modified: string
  color?: number
  gallery?: string[]
  /** Chinese project name from the bundled MC百科 index. */
  nameTranslation?: string
}

export interface ModrinthSearchResult {
  hits: ModrinthHit[]
  offset: number
  limit: number
  total_hits: number
}

export interface ModrinthProject extends Omit<ModrinthHit, 'gallery'> {
  /** Project detail endpoint also exposes its own id and followers count. */
  id?: string
  followers?: number
  body: string
  gallery: { url: string; featured: boolean; title?: string; description?: string }[]
  issues_url?: string
  source_url?: string
  wiki_url?: string
  discord_url?: string
  license?: { id: string; name: string; url?: string }
  client_side: string
  server_side: string
  updated: string
  published: string
}

export interface ModrinthVersionFile {
  url: string
  filename: string
  hashes: Record<string, string>
  size: number
  primary: boolean
}

export interface ModrinthVersion {
  id: string
  project_id: string
  name: string
  version_number: string
  game_versions: string[]
  loaders: string[]
  date_published: string
  downloads: number
  version_type: 'release' | 'beta' | 'alpha'
  files: ModrinthVersionFile[]
  dependencies: { version_id?: string; project_id?: string; file_name?: string; dependency_type: 'required' | 'optional' | 'incompatible' | 'embedded' }[]
}

export type ContentFilterKind = 'mod' | 'resourcepack' | 'shader' | 'datapack' | 'modpack'

export interface NewsItem {
  id: string
  title: string
  summary: string
  url?: string
  date?: string
  image?: string
  tag?: string
  pinned?: boolean
}

export interface GameState {
  instanceId?: string
  instanceName?: string
  state: 'idle' | 'preparing' | 'running' | 'exited' | 'crashed'
  pid?: number
  startedAt?: number
  exitCode?: number
  crashReport?: string
}

export interface LogFileInfo {
  name: string
  path: string
  size: number
  mtime: number
  kind: 'latest' | 'crash' | 'launcher'
}

export interface CrashAnalysis {
  cause: string
  confidence: 'high' | 'medium' | 'low'
  summary: string
  suggestions: string[]
  evidence: string[]
  relatedMods: string[]
}

export interface DeviceCodeInfo {
  flowId: string
  userCode: string
  verificationUri: string
  expiresIn: number
  interval: number
  message?: string
}

export interface MsPollResult {
  status: 'pending' | 'success' | 'error' | 'expired'
  message?: string
  account?: Account
}

export interface AppInfo {
  version: string
  electron: string
  chrome: string
  node: string
  platform: string
  arch: string
  rootDir: string
  versionsDir: string
  assetsDir: string
  librariesDir: string
  instancesDir: string
  totalInstances: number
  totalVersions: number
  totalPlayTimeMs: number
}

export interface FilePickResult {
  canceled: boolean
  paths: string[]
}

export interface LaunchOptions {
  instanceId: string
  accountId?: string
  server?: string
  world?: string
  width?: number
  height?: number
  fullscreen?: boolean
}

export interface LaunchResult {
  ok: boolean
  error?: string
  pid?: number
}

export interface DownloadInstallOptions {
  projectId: string
  instanceId: string
  versionId?: string
  versionName?: string
  withDependencies?: boolean
  targetKind?: ContentKind
}

export interface InstallLoaderRequest {
  loader: 'fabric' | 'quilt' | 'forge' | 'neoforge'
  mcVersion: string
  loaderVersion?: string
  instanceName?: string
  createInstance?: boolean
}

export interface AppEvents {
  'progress:update': ProgressSnapshot
  'log:line': LogLine
  'game:state': GameState
  'settings:changed': Settings
  'accounts:changed': Account[]
  'instances:changed': Instance[]
  'toast': { type: 'info' | 'success' | 'error' | 'warning'; message: string; title?: string }
  'console:toggle': boolean
}

export interface EndpointTestResult {
  id: string
  label: string
  url: string
  ok: boolean
  ms: number
  status?: number
  error?: string
}

// ---------------------------------------------------------------------------
// CurseForge
// ---------------------------------------------------------------------------

export interface CurseForgeHit {
  id: number
  name: string
  slug: string
  summary: string
  logo?: string
  downloads: number
  categories: string[]
  authors: string[]
  classId: number
  projectType: ContentFilterKind
  dateModified: string
  /** Chinese project name from the bundled MC百科 index. */
  nameTranslation?: string
}

export interface CurseForgeProject extends CurseForgeHit {
  description: string
  screenshots: string[]
  latestFiles: CurseForgeFile[]
  links?: { websiteUrl?: string; sourceUrl?: string; issuesUrl?: string }
}

export interface CurseForgeFile {
  id: number
  modId: number
  displayName: string
  fileName: string
  releaseType: number
  fileDate: string
  fileLength: number
  downloadCount: number
  gameVersions: string[]
  downloadUrl?: string | null
  hashes: { algo: number; value: string }[]
  dependencies: { modId: number; relationType: number }[]
}

export interface CurseForgeSearchResult {
  hits: CurseForgeHit[]
  total: number
  offset: number
  limit: number
}

export interface CurseForgeInstallOptions {
  instanceId: string
  modId: number
  fileId: number
}

export interface CurseForgePackInstallOptions {
  modId: number
  fileId?: number
  instanceName?: string
}

export interface CurseForgeCategory {
  id: number
  name: string
}
