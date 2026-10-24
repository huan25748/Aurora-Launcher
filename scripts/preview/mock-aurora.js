/* Mock of window.aurora used by the headless UI preview. */
(function () {
  const now = Date.now()
  const instances = [
    {
      id: 'inst-demo',
      name: '暮色生存 · 1.20.1',
      versionId: 'fabric-loader-0.16.9-1.20.1',
      mcVersion: '1.20.1',
      loader: 'fabric',
      loaderVersion: '0.16.9',
      gameDir: 'C:\\Aurora\\minecraft\\instances\\inst-demo',
      icon: '🌿',
      color: 'forest',
      memory: 6144,
      createdAt: now - 86400000 * 42,
      lastPlayedAt: now - 1000 * 60 * 26,
      totalPlayTimeMs: 1000 * 60 * 60 * 46,
      playCount: 87
    },
    {
      id: 'inst-tech',
      name: '机械动力工厂',
      versionId: 'neoforge-21.1.72',
      mcVersion: '1.21.1',
      loader: 'neoforge',
      loaderVersion: '21.1.72',
      gameDir: 'C:\\Aurora\\minecraft\\instances\\inst-tech',
      icon: '⚙️',
      color: 'ocean',
      memory: 8192,
      createdAt: now - 86400000 * 12,
      lastPlayedAt: now - 86400000 * 2,
      totalPlayTimeMs: 1000 * 60 * 60 * 12,
      playCount: 15
    },
    {
      id: 'inst-old',
      name: '怀旧 1.12.2',
      versionId: '1.12.2-forge-14.23.5.2860',
      mcVersion: '1.12.2',
      loader: 'forge',
      loaderVersion: '14.23.5.2860',
      gameDir: 'C:\\Aurora\\minecraft\\instances\\inst-old',
      icon: '🔥',
      color: 'ember',
      memory: 4096,
      createdAt: now - 86400000 * 90,
      playCount: 3,
      totalPlayTimeMs: 1000 * 60 * 60 * 4
    }
  ]
  const accounts = [
    { id: 'acc-ms', type: 'microsoft', username: 'Steve_', uuid: '069a79f444e94726a5befca90e38aaf5', accessToken: 'x', createdAt: now, lastUsedAt: now },
    { id: 'acc-off', type: 'offline', username: 'Alex', uuid: 'a1b2c3', accessToken: '0', createdAt: now }
  ]
  const settings = {
    rootDir: 'C:\\Aurora\\minecraft',
    language: 'zh-CN',
    theme: 'dark',
    accent: 'aurora',
    downloadSource: 'auto',
    maxConcurrency: 24,
    proxyEnabled: false,
    proxyHost: '127.0.0.1',
    proxyPort: 7890,
    minimizeOnLaunch: false,
    showConsoleOnLaunch: true,
    keepLauncherOpen: true,
    closeBehavior: 'exit',
    defaultMemory: 6144,
    autoMemory: false,
    jvmArgs: '',
    gameArgs: '',
    javaPath: '',
    msClientId: '',
    enableStats: true,
    lastSelectedInstanceId: 'inst-demo',
    onboardingDone: true,
    checkUpdates: true,
    downloadAssets: true,
    downloadNatives: true,
    useAuthlibForOffline: false
  }
  const manifestVersions = [
    ['1.21.4', 'release', '2024-12-03'],
    ['1.21.3', 'release', '2024-10-23'],
    ['1.21.1', 'release', '2024-08-08'],
    ['1.20.6', 'release', '2024-04-29'],
    ['1.20.1', 'release', '2023-06-12'],
    ['1.12.2', 'release', '2017-09-18'],
    ['26.4-snapshot-5', 'snapshot', '2026-10-20'],
    ['26.4-snapshot-4', 'snapshot', '2026-10-14'],
    ['1.8.9', 'release', '2015-12-09'],
    ['b1.7.3', 'old_beta', '2011-07-08']
  ].map(([id, type, date]) => ({ id, type, releaseTime: date + 'T00:00:00+00:00', url: '', sha1: '' }))
  const news = [
    {
      id: 'n1',
      title: 'Minecraft 26.4 Snapshot 5 发布',
      summary: '新的生物群系与地形生成调整，性能优化与漏洞修复。',
      tag: '快照',
      date: '2026-10-20'
    },
    {
      id: 'n2',
      title: 'Aurora Launcher 1.0 正式发布',
      summary: '全新界面、内置 Modrinth 资源中心，以及更快的并行下载。',
      tag: '公告',
      date: '2026-10-18'
    },
    {
      id: 'n3',
      title: 'Fabric Loader 0.16.9 更新',
      summary: '修复若干与 1.20.1 相关的 Mixin 兼容问题。',
      tag: '加载器',
      date: '2026-10-12'
    }
  ]
  const modrinth = [
    { project_id: 'P7dR8mSH', slug: 'fabric-api', title: 'Fabric API', description: 'Fabric 生态的必备前置库，提供大量通用 API。', categories: ['fabric', 'library'], versions: ['1.20.1', '1.21.1'], downloads: 252026850, follows: 42057, author: 'fabricmc', project_type: 'mod', icon_url: '', date_modified: '2026-10-01', color: 8703084 },
    { project_id: 'AANobbMI', slug: 'sodium', title: 'Sodium', description: '高性能渲染引擎替换，大幅提升帧率并减少卡顿。', categories: ['fabric', 'optimization'], versions: ['1.20.1', '1.21.1'], downloads: 95000000, follows: 31200, author: 'CaffeineMC', project_type: 'mod', icon_url: '', date_modified: '2026-10-02' },
    { project_id: 'YL57xq9U', slug: 'iris', title: 'Iris Shaders', description: '兼容 OptiFine 光影的现代光影加载器。', categories: ['fabric', 'shaders'], versions: ['1.20.1', '1.21.1'], downloads: 42000000, follows: 18000, author: 'IrisShaders', project_type: 'mod', icon_url: '', date_modified: '2026-09-28' },
    { project_id: '1IjD5062', slug: 'continuity', title: 'Continuity', description: '连接纹理与自定义方块模型支持。', categories: ['fabric', 'decoration'], versions: ['1.20.1'], downloads: 7200000, follows: 4200, author: 'Pepperbell', project_type: 'mod', icon_url: '', date_modified: '2026-09-02' },
    { project_id: '9s6osm5g', slug: 'lithium', title: 'Lithium', description: '现代化通用优化模组，不改变原版机制。', categories: ['fabric', 'optimization'], versions: ['1.20.1'], downloads: 38000000, follows: 12000, author: 'CaffeineMC', project_type: 'mod', icon_url: '', date_modified: '2026-08-19' },
    { project_id: 'EsAfCjCV', slug: 'cobblemon', title: 'Cobblemon', description: '将宝可梦带入 Minecraft 的大型整合模组。', categories: ['fabric', 'adventure'], versions: ['1.20.1'], downloads: 15000000, follows: 9000, author: 'Cobblemon', project_type: 'mod', icon_url: '', date_modified: '2026-07-30' }
  ]
  const javaList = [
    { path: 'C:\\Aurora\\minecraft\\runtime\\java-runtime-gamma\\bin\\java.exe', version: '17.0.15', major: 17, arch: 'x64', vendor: 'Mojang', is64Bit: true, source: 'managed', managed: true },
    { path: 'C:\\Program Files\\Java\\jdk-21\\bin\\java.exe', version: '21.0.5', major: 21, arch: 'x64', vendor: 'Eclipse Temurin', is64Bit: true, source: 'scan' },
    { path: 'C:\\Program Files (x86)\\Java\\jre1.8.0_411\\bin\\java.exe', version: '1.8.0_411', major: 8, arch: 'x86', vendor: 'Oracle', is64Bit: false, source: 'scan' }
  ]
  const mods = [
    { name: 'Sodium', fileName: 'sodium-fabric-0.5.11.jar', path: '', enabled: true, size: 812345, mtime: now, kind: 'mods', meta: { id: 'sodium', name: 'Sodium', version: '0.5.11', description: '高性能渲染引擎替换', authors: ['CaffeineMC'], loader: 'fabric' } },
    { name: 'Iris Shaders', fileName: 'iris-1.7.5.jar', path: '', enabled: true, size: 2456789, mtime: now, kind: 'mods', meta: { id: 'iris', name: 'Iris Shaders', version: '1.7.5', description: '现代光影加载器', loader: 'fabric' } },
    { name: 'Fabric API', fileName: 'fabric-api-0.92.2.jar', path: '', enabled: true, size: 2312345, mtime: now, kind: 'mods', meta: { id: 'fabric-api', name: 'Fabric API', version: '0.92.2', description: 'Fabric 必备前置', loader: 'fabric' } },
    { name: 'Lithium', fileName: 'lithium-0.12.1.jar', path: '', enabled: false, size: 654321, mtime: now, kind: 'mods', meta: { id: 'lithium', name: 'Lithium', version: '0.12.1', description: '通用性能优化', loader: 'fabric' } }
  ]
  const worlds = [
    { name: '暮色森林生存', folderName: 'world1', path: '', size: 734003200, mtime: now, lastPlayed: now - 86400000, gameVersion: '1.20.1', hardcore: false, cheats: true },
    { name: '创造建筑档', folderName: 'world2', path: '', size: 220200960, mtime: now - 86400000 * 8, gameVersion: '1.20.1', cheats: true },
    { name: 'Hardcore', folderName: 'world3', path: '', size: 67108864, mtime: now - 86400000 * 20, gameVersion: '1.20.1', hardcore: true }
  ]
  const progress = {
    groups: [
      {
        id: 'g1',
        title: '启动游戏',
        status: 'running',
        startedAt: now - 12000,
        steps: [
          { id: 's1', title: '准备启动信息', current: 1, total: 1, status: 'done', detail: 'Java 17.0.15' },
          { id: 's2', title: '检查游戏文件', current: 1, total: 1, status: 'done' },
          { id: 's3', title: '下载游戏资源', current: 2380, total: 4120, status: 'running', detail: '2380/4120 · 412 MB' },
          { id: 's4', title: '解压本地库', current: 0, total: 1, status: 'pending' }
        ]
      },
      {
        id: 'g2',
        title: '安装 Fabric 0.16.9 (1.20.1)',
        status: 'done',
        startedAt: now - 60000,
        finishedAt: now - 30000,
        steps: [{ id: 's1', title: '下载加载器依赖', current: 9, total: 9, status: 'done' }]
      }
    ],
    activeGroupId: 'g1'
  }
  const logs = [
    { ts: now - 15000, source: 'launcher', level: 'info', text: '启动 暮色生存 · 1.20.1 (fabric-loader-0.16.9-1.20.1) · Java 17.0.15' },
    { ts: now - 14800, source: 'game', level: 'info', text: 'Loading Minecraft 1.20.1 with Fabric Loader 0.16.9' },
    { ts: now - 14000, source: 'game', level: 'info', text: 'Loading 62 mods:' },
    { ts: now - 13900, source: 'game', level: 'info', text: '  - sodium 0.5.11' },
    { ts: now - 13800, source: 'game', level: 'info', text: '  - iris 1.7.5' },
    { ts: now - 12000, source: 'game', level: 'info', text: 'OpenGL 4.6.0 NVIDIA 566.36' },
    { ts: now - 9000, source: 'game', level: 'warn', text: '[Render thread/WARN] Unsupported shader pack option' },
    { ts: now - 5000, source: 'game', level: 'info', text: 'Sound engine started' },
    { ts: now - 3000, source: 'game', level: 'info', text: 'Created: 1024x512x4 minecraft:textures/atlas/blocks.png-atlas' }
  ]
  const servers = [
    { name: 'Hypixel', ip: 'mc.hypixel.net' },
    { name: 'Aurora 生存服', ip: 'play.aurora.example:25565' }
  ]
  const wrap = (value) => () => Promise.resolve(value)
  const noop = () => Promise.resolve()
  const event = () => () => {}
  window.aurora = {
    app: {
      info: async () => ({
        version: '1.0.0',
        electron: '33.4.11',
        chrome: '130.0.6723.191',
        node: '20.18.3',
        platform: 'win32',
        arch: 'x64',
        rootDir: settings.rootDir,
        versionsDir: settings.rootDir + '\\versions',
        assetsDir: settings.rootDir + '\\assets',
        librariesDir: settings.rootDir + '\\libraries',
        instancesDir: settings.rootDir + '\\instances',
        totalInstances: instances.length,
        totalVersions: 4,
        totalPlayTimeMs: 1000 * 60 * 60 * 62
      }),
      openExternal: noop,
      openPath: noop,
      showItem: noop,
      pickDirectory: async () => ({ canceled: true, paths: [] }),
      pickFile: async () => ({ canceled: true, paths: [] }),
      relaunch: noop,
      quit: noop,
      window: { minimize: noop, toggleMaximize: async () => false, close: noop, isMaximized: async () => false, setTitle: noop }
    },
    settings: {
      get: async () => settings,
      update: async (patch) => Object.assign(settings, patch),
      reset: async () => settings,
      pickRoot: async () => settings,
      systemInfo: async () => ({
        platform: 'win32',
        arch: 'x64',
        os: 'Windows 11 26H2',
        cpu: 'AMD Ryzen 9 7950X 16-Core',
        cores: 32,
        memoryMB: 65536,
        freeMemoryMB: 32768,
        suggestedMemory: 8192,
        appVersion: '1.0.0',
        electron: '33.4.11',
        chrome: '130',
        node: '20.18.3',
        configPath: 'C:\\Users\\demo\\AppData\\Roaming\\AuroraLauncher\\config.json'
      })
    },
    versions: {
      manifest: async () => ({ latest: { release: '1.21.4', snapshot: '26.4-snapshot-5' }, versions: manifestVersions, fetchedAt: now }),
      installed: async () => [
        { id: 'fabric-loader-0.16.9-1.20.1', type: 'release', mcVersion: '1.20.1', loader: 'fabric', loaderVersion: '0.16.9', hasJar: false, sizeBytes: 0 },
        { id: 'neoforge-21.1.72', type: 'release', mcVersion: '1.21.1', loader: 'neoforge', loaderVersion: '21.1.72', hasJar: false, sizeBytes: 0 },
        { id: '1.12.2-forge-14.23.5.2860', type: 'release', mcVersion: '1.12.2', loader: 'forge', loaderVersion: '14.23.5.2860', hasJar: false, sizeBytes: 0 }
      ],
      loaders: async () => [
        { version: '0.16.9', stable: true, mcVersion: '1.20.1' },
        { version: '0.16.8', stable: true, mcVersion: '1.20.1' },
        { version: '0.15.11', stable: true, mcVersion: '1.20.1' },
        { version: '0.14.24', stable: true, mcVersion: '1.20.1' }
      ],
      delete: noop,
      javaRequirement: async () => 17,
      loaderSize: async () => ({ size: 0, files: 0 })
    },
    install: {
      vanilla: async () => ({ versionId: '1.20.1', instanceId: 'inst-demo' }),
      loader: async () => ({ versionId: 'fabric-loader-0.16.9-1.20.1' })
    },
    launch: { start: async () => ({ ok: true, pid: 4242 }), stop: noop, state: async () => ({ state: 'idle' }) },
    instances: {
      list: async () => instances,
      get: async (id) => instances.find((i) => i.id === id) || null,
      create: async (input) => instances[0],
      update: async (id, patch) => Object.assign(instances.find((i) => i.id === id) || {}, patch),
      remove: noop,
      copy: async () => instances[0],
      export: async () => ({ path: 'C:\\Aurora\\export.zip' }),
      import: async () => null,
      openFolder: noop,
      size: async () => ({ size: 734003200, files: 1234 })
    },
    content: {
      list: async (id, kind) => (kind === 'mods' ? mods : []),
      worlds: async () => worlds,
      screenshots: async () => [
        { fileName: '2026-10-20_18.22.11.png', path: '', size: 1200000, mtime: now - 86400000 },
        { fileName: '2026-10-18_21.03.44.png', path: '', size: 980000, mtime: now - 86400000 * 3 }
      ],
      toggle: noop,
      remove: noop,
      import: async () => 0,
      backupWorld: async () => ({ path: 'C:\\Aurora\\backup.zip' }),
      deleteWorld: noop
    },
    modnames: {
      translate: async () => ({})
    },
    modrinth: {
      search: async () => ({ hits: modrinth, offset: 0, limit: 20, total_hits: 6 }),
      featured: async () => modrinth.slice(0, 4),
      project: async (id) => ({
        ...(modrinth.find((m) => m.project_id === id) || modrinth[0]),
        body: '# Sodium\n\nSodium is a modern rendering engine for Minecraft which greatly improves performance and fixes many graphical issues.\n\n- 大幅提升帧率\n- 减少内存占用\n- 兼容光影\n',
        gallery: [],
        client_side: 'required',
        server_side: 'unsupported',
        updated: '2026-10-01',
        published: '2021-01-03',
        license: { id: 'LGPL-3.0', name: 'LGPL-3.0' }
      }),
      versions: async () => [
        { id: 'v1', project_id: 'AANobbMI', name: 'Sodium 0.5.11', version_number: '0.5.11', game_versions: ['1.20.1'], loaders: ['fabric'], date_published: '2026-09-20', downloads: 1200000, version_type: 'release', files: [{ url: '', filename: 'sodium-fabric-0.5.11.jar', hashes: {}, size: 812345, primary: true }], dependencies: [] }
      ],
      install: async () => ({ installed: ['sodium.jar'], skipped: [] }),
      installPack: async () => ({ instanceId: 'inst-demo' })
    },
    java: { list: async () => javaList, scan: async () => javaList, download: async (m) => javaList[0], delete: noop },
    accounts: {
      list: async () => accounts,
      active: async () => accounts[0],
      setActive: async (id) => accounts.find((a) => a.id === id),
      addOffline: async (name) => ({ id: 'acc-new', type: 'offline', username: name, uuid: 'deadbeef', accessToken: '0', createdAt: now }),
      startMicrosoft: async () => ({ flowId: 'flow', userCode: 'ABCD-EFGH', verificationUri: 'https://microsoft.com/link', expiresIn: 900, interval: 5 }),
      pollMicrosoft: async () => ({ status: 'pending' }),
      cancelMicrosoft: noop,
      addAuthlib: async (s, u) => ({ id: 'acc-al', type: 'authlib', username: u, uuid: 'al', accessToken: 'x', authServer: s, authServerName: '第三方' }),
      remove: noop,
      refresh: async () => accounts[0]
    },
    servers: {
      list: async () => servers,
      add: async () => servers,
      remove: async () => servers,
      ping: async () => ({ online: true, version: { name: '1.20.1', protocol: 763 }, players: { online: 21, max: 100 }, motd: { text: 'Aurora Network' }, ping: 32 })
    },
    logs: { files: async () => [], read: async () => '', latest: async () => logs, analyze: async () => null, clearConsole: noop },
    news: { list: async () => news },
    events: {
      onProgress: (cb) => { setTimeout(() => cb(progress), 700); return () => {} },
      onLog: event,
      onGameState: event,
      onSettings: event,
      onAccounts: event,
      onInstances: event,
      onToast: event,
      onConsoleToggle: event,
      onMaximized: event
    }
  }
})()
