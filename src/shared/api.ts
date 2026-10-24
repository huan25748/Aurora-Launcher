import type {
  Account,
  AppInfo,
  ContentItem,
  CurseForgeCategory,
  CurseForgeFile,
  CurseForgeHit,
  CurseForgeInstallOptions,
  CurseForgePackInstallOptions,
  CurseForgePackInstallResult,
  CurseForgeProject,
  CurseForgeSearchResult,
  ContentKind,
  CrashAnalysis,
  DeviceCodeInfo,
  DownloadInstallOptions,
  EndpointTestResult,
  FilePickResult,
  GameState,
  InstallLoaderRequest,
  Instance,
  InstalledVersion,
  JavaInfo,
  LaunchOptions,
  LaunchResult,
  LoaderVersion,
  LogFileInfo,
  LogLine,
  McVersionSummary,
  ModrinthHit,
  ModrinthProject,
  ModrinthSearchResult,
  ModrinthVersion,
  MsPollResult,
  NewsItem,
  ProgressSnapshot,
  ScreenshotItem,
  ServerEntry,
  ServerStatus,
  Settings,
  StorageStats,
  UpdateInfo,
  VersionManifest,
  WorldItem
} from './types'

export interface WindowControls {
  minimize(): Promise<void>
  toggleMaximize(): Promise<boolean>
  close(): Promise<void>
  isMaximized(): Promise<boolean>
  setTitle(title: string): Promise<void>
}

export interface AuroraApi {
  network: {
    test(): Promise<EndpointTestResult[]>
  }
  app: {
    info(): Promise<AppInfo>
    openExternal(url: string): Promise<void>
    openPath(target: string): Promise<void>
    showItem(path: string): Promise<void>
    pickDirectory(title?: string, defaultPath?: string): Promise<FilePickResult>
    pickFile(filters?: { name: string; extensions: string[] }[], multi?: boolean): Promise<FilePickResult>
    window: WindowControls
    relaunch(): Promise<void>
    quit(): Promise<void>
  }
  settings: {
    get(): Promise<Settings>
    update(patch: Partial<Settings>): Promise<Settings>
    reset(): Promise<Settings>
    pickRoot(): Promise<Settings>
    usePortableRoot(): Promise<Settings>
    systemInfo(): Promise<Record<string, unknown>>
  }
  versions: {
    manifest(force?: boolean): Promise<VersionManifest>
    installed(): Promise<InstalledVersion[]>
    delete(versionId: string): Promise<void>
    loaders(kind: 'fabric' | 'quilt' | 'forge' | 'neoforge', mcVersion: string): Promise<LoaderVersion[]>
    javaRequirement(versionId: string): Promise<number>
    loaderSize(versionId: string): Promise<{ size: number; files: number }>
  }
  install: {
    vanilla(versionId: string, createInstance?: boolean, instanceName?: string): Promise<{ instanceId?: string; versionId: string }>
    loader(req: InstallLoaderRequest): Promise<{ instanceId?: string; versionId: string }>
  }
  launch: {
    start(opts: LaunchOptions): Promise<LaunchResult>
    stop(): Promise<void>
    state(): Promise<GameState>
  }
  instances: {
    list(): Promise<Instance[]>
    get(id: string): Promise<Instance | null>
    create(input: { name: string; versionId: string; mcVersion: string; loader: string; loaderVersion?: string; icon?: string; color?: string }): Promise<Instance>
    update(id: string, patch: Partial<Instance>): Promise<Instance>
    remove(id: string, deleteFiles: boolean): Promise<void>
    copy(id: string, name: string): Promise<Instance>
    export(id: string): Promise<{ path: string }>
    exportPack(id: string): Promise<{ path: string; size: number; files: number }>
    import(): Promise<Instance | null>
    importExisting(): Promise<number>
    openFolder(id: string, sub?: string): Promise<void>
    size(id: string): Promise<{ size: number; files: number }>
  }
  content: {
    list(instanceId: string, kind: ContentKind): Promise<ContentItem[]>
    worlds(instanceId: string): Promise<WorldItem[]>
    screenshots(instanceId: string): Promise<ScreenshotItem[]>
    toggle(instanceId: string, kind: ContentKind, fileName: string): Promise<void>
    remove(instanceId: string, kind: ContentKind, fileName: string): Promise<void>
    import(instanceId: string, kind: ContentKind): Promise<number>
    backupWorld(instanceId: string, folderName: string): Promise<{ path: string }>
    deleteWorld(instanceId: string, folderName: string): Promise<void>
  }
  modrinth: {
    search(params: {
      query?: string
      projectType?: 'mod' | 'resourcepack' | 'shader' | 'datapack' | 'modpack'
      mcVersion?: string
      loader?: string
      offset?: number
      limit?: number
      index?: 'relevance' | 'downloads' | 'follows' | 'newest' | 'updated'
      categories?: string[]
    }): Promise<ModrinthSearchResult>
    project(id: string): Promise<ModrinthProject>
    versions(projectId: string, mcVersion?: string, loader?: string): Promise<ModrinthVersion[]>
    translations(ids: string[]): Promise<Record<string, string>>
    install(opts: DownloadInstallOptions): Promise<{ installed: string[]; skipped: string[] }>
    installToFolder(opts: { projectId: string; versionId: string; destDir: string }): Promise<{ installed: string[] }>
    installPack(projectId: string, instanceName?: string, versionId?: string): Promise<{ instanceId: string }>
    featured(projectType?: string): Promise<ModrinthHit[]>
  }
  updates: {
    check(force?: boolean): Promise<UpdateInfo>
  }
  storage: {
    stats(): Promise<StorageStats>
    clean(): Promise<{ freed: number }>
    prune(): Promise<{ freed: number; libraries: number; assets: number }>
  }
  modnames: {
    translate(queries: { name: string; slug?: string; source: 'modrinth' | 'curseforge' }[]): Promise<Record<string, string>>
  }
  curseforge: {
    categories(type: 'mod' | 'resourcepack' | 'shader' | 'datapack' | 'modpack'): Promise<CurseForgeCategory[]>
    search(params: { query?: string; categoryId?: number; projectType: 'mod' | 'resourcepack' | 'shader' | 'datapack' | 'modpack'; mcVersion?: string; loader?: string; offset?: number; limit?: number; sort?: 'relevance' | 'downloads' | 'newest' | 'updated' }): Promise<CurseForgeSearchResult>
    project(id: number): Promise<CurseForgeProject>
    files(id: number, mcVersion?: string, loader?: string): Promise<CurseForgeFile[]>
    translations(ids: number[]): Promise<Record<string, string>>
    install(opts: CurseForgeInstallOptions): Promise<{ installed: string[]; skipped: string[] }>
    installToFolder(opts: { modId: number; fileId: number; destDir: string }): Promise<{ installed: string[] }>
    installPack(opts: CurseForgePackInstallOptions): Promise<CurseForgePackInstallResult>
  }
  java: {
    list(): Promise<JavaInfo[]>
    scan(): Promise<JavaInfo[]>
    download(major: number): Promise<JavaInfo>
    delete(path: string): Promise<void>
  }
  accounts: {
    list(): Promise<Account[]>
    active(): Promise<Account | null>
    setActive(id: string): Promise<Account | null>
    addOffline(username: string): Promise<Account>
    startMicrosoft(): Promise<DeviceCodeInfo>
    pollMicrosoft(flowId: string): Promise<MsPollResult>
    cancelMicrosoft(flowId: string): Promise<void>
    addAuthlib(server: string, username: string, password: string): Promise<Account>
    remove(id: string): Promise<void>
    refresh(id: string): Promise<Account | null>
  }
  servers: {
    list(instanceId: string): Promise<ServerEntry[]>
    add(instanceId: string, entry: ServerEntry): Promise<ServerEntry[]>
    remove(instanceId: string, ip: string): Promise<ServerEntry[]>
    ping(ip: string): Promise<ServerStatus>
  }
  logs: {
    files(instanceId: string): Promise<LogFileInfo[]>
    read(path: string, maxBytes?: number): Promise<string>
    latest(instanceId?: string): Promise<LogLine[]>
    analyze(instanceId: string): Promise<CrashAnalysis | null>
    clearConsole(): Promise<void>
  }
  news: {
    list(force?: boolean): Promise<NewsItem[]>
  }
  events: {
    onProgress(cb: (snapshot: ProgressSnapshot) => void): () => void
    onLog(cb: (line: LogLine) => void): () => void
    onGameState(cb: (state: GameState) => void): () => void
    onSettings(cb: (settings: Settings) => void): () => void
    onAccounts(cb: (accounts: Account[]) => void): () => void
    onInstances(cb: (instances: Instance[]) => void): () => void
    onToast(cb: (toast: { type: string; message: string; title?: string }) => void): () => void
    onConsoleToggle(cb: (open: boolean) => void): () => void
    onMaximized(cb: (max: boolean) => void): () => void
  }
}
