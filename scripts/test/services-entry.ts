import fs from 'node:fs'
import path from 'node:path'
import { initSettings } from '../../src/main/core/settings'
import { ensureDirs, dirs } from '../../src/main/core/paths'
import { startMicrosoftLogin, cancelMicrosoftLogin, authlibJar, listAccounts } from '../../src/main/minecraft/auth'
import { getNews } from '../../src/main/minecraft/news'
import { pingServer } from '../../src/main/minecraft/servers'
import { getProject, installProject, searchModrinth } from '../../src/main/minecraft/modrinth'
import { listInstances } from '../../src/main/minecraft/instances'
import { listLoaderVersions } from '../../src/main/minecraft/installer'

const results: { name: string; ok: boolean; detail?: string }[] = []
function check(name: string, ok: boolean, detail?: string): void {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`)
  if (!ok) process.exitCode = 1
}
async function safe(name: string, fn: () => Promise<void>): Promise<void> {
  try {
    await fn()
  } catch (e) {
    check(name, false, e instanceof Error ? `${e.name}: ${e.message}` : String(e))
  }
}

async function main(): Promise<void> {
  initSettings()
  ensureDirs()

  console.log('\n=== microsoft device flow ===')
  await safe('microsoft device flow', async () => {
    const flow = await startMicrosoftLogin()
    check('device code issued', !!flow.userCode && !!flow.verificationUri, `${flow.userCode} @ ${flow.verificationUri}`)
    check('device code lifetime', flow.expiresIn > 60, `${flow.expiresIn}s`)
    cancelMicrosoftLogin(flow.flowId)
  })

  console.log('\n=== news ===')
  await safe('news', async () => {
    const news = await getNews(true)
    check('news fetched', news.length >= 3, `${news.length} entries, first: ${news.find((n) => n.image)?.title ?? news[0]?.title}`)
  })

  console.log('\n=== authlib-injector ===')
  await safe('authlib', async () => {
    const jar = await authlibJar()
    check('authlib-injector downloaded', fs.existsSync(jar) && fs.statSync(jar).size > 100_000, `${path.basename(jar)} ${fs.statSync(jar).size} bytes`)
  })

  console.log('\n=== loader lists ===')
  await safe('forge list', async () => {
    const forgeVersions = await listLoaderVersions('forge', '1.20.1')
    check('forge versions', forgeVersions.length > 0, `${forgeVersions.length} versions, latest ${forgeVersions[0]?.version}`)
  })
  await safe('neoforge list', async () => {
    const neoVersions = await listLoaderVersions('neoforge', '1.21.1')
    check('neoforge versions', neoVersions.length > 0, `${neoVersions.length} versions, latest ${neoVersions[0]?.version}`)
  })
  await safe('neoforge legacy list', async () => {
    const neoLegacy = await listLoaderVersions('neoforge', '1.20.1')
    check('neoforge legacy versions', neoLegacy.length > 0, `${neoLegacy.length} versions, latest ${neoLegacy[0]?.version}`)
  })
  await safe('quilt list', async () => {
    const quiltVersions = await listLoaderVersions('quilt', '1.20.1')
    check('quilt versions', quiltVersions.length > 0, `${quiltVersions.length} versions, latest ${quiltVersions[0]?.version}`)
  })

  console.log('\n=== server ping ===')
  for (const address of ['mc.hypixel.net', 'play.purpleprison.net']) {
    await safe(`ping ${address}`, async () => {
      const status = await pingServer(address, 6000)
      check(`ping ${address}`, status.online || !!status.error, status.online ? `${status.players?.online}/${status.players?.max} online, ${status.ping}ms` : `offline: ${status.error}`)
    })
  }

  console.log('\n=== modrinth catalog ===')
  await safe('modrinth catalog', async () => {
    const page1 = await searchModrinth({ projectType: 'mod', limit: 40, offset: 0, index: 'downloads' })
    const page2 = await searchModrinth({ projectType: 'mod', limit: 40, offset: 40, index: 'downloads' })
    check('full catalog is searchable', page1.total_hits > 1000, `total ${page1.total_hits}`)
    check('page size 40', page1.hits.length === 40, String(page1.hits.length))
    check('pagination works', page2.hits.length === 40 && page2.hits[0].project_id !== page1.hits[0].project_id, `${page1.hits[0].project_id} -> ${page2.hits[0].project_id}`)
    const project = await getProject('AANobbMI')
    check('project id normalized', project.project_id === 'AANobbMI' && project.id === 'AANobbMI', `id=${project.id} project_id=${project.project_id}`)
    check('project numbers normalized', project.downloads > 0 && project.follows > 0 && project.followers > 0, `downloads=${project.downloads} follows=${project.follows} followers=${project.followers}`)
    check('project categories present', Array.isArray(project.categories) && project.categories.length > 0, project.categories.join(','))
  })

  console.log('\n=== modrinth install ===')
  await safe('modrinth install', async () => {
    const instances = await listInstances()
    const fabric = instances.find((i) => i.loader === 'fabric')
    if (!fabric) {
      check('fabric instance available', false)
      return
    }
    const res = await installProject({ projectId: 'AANobbMI', instanceId: fabric.id, withDependencies: true })
    check('sodium installed', res.installed.length > 0 || res.skipped.length > 0, res.installed.concat(res.skipped).join(', '))
    const files = fs.readdirSync(path.join(fabric.gameDir, 'mods')).filter((f) => /sodium/i.test(f))
    check('sodium file present', files.length > 0, files.join(', '))
  })

  console.log('\n=== accounts store ===')
  const accounts = listAccounts()
  check('accounts persisted', accounts.length >= 1, accounts.map((a) => `${a.username}(${a.type})`).join(', '))

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} checks passed`)
  if (failed.length) console.log('FAILED:', failed.map((f) => f.name).join(', '))
}

main().catch((e) => {
  console.error('SERVICES TEST FAILED:', e)
  process.exitCode = 1
})
