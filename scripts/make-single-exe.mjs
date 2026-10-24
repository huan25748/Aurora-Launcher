// Builds release/AuroraLauncher.exe: a single self-extracting executable.
// Layout: [C# stub][ZIP payload][int64 length][8-byte "AURORASF"]
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const releaseDir = path.join(root, 'release', 'AuroraLauncher')
const workDir = path.join(root, 'dist', 'sfx')
const cscCandidates = [
  'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe',
  'C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe'
]
const csc = cscCandidates.find((candidate) => fs.existsSync(candidate)) ?? cscCandidates[0]

function run(file, args, cwd = root) {
  const result = spawnSync(file, args, { stdio: 'inherit', cwd })
  if (result.status !== 0) {
    console.error(`command failed (${result.status}): ${file} ${args.join(' ')}`)
    process.exit(result.status ?? 1)
  }
}

function makeIco(pngFile, icoFile) {
  const png = fs.readFileSync(pngFile)
  const header = Buffer.alloc(6)
  header.writeUInt16LE(0, 0)
  header.writeUInt16LE(1, 2)
  header.writeUInt16LE(1, 4)
  const entry = Buffer.alloc(16)
  entry[0] = 0 // width 256
  entry[1] = 0 // height 256
  entry[2] = 0
  entry[3] = 0
  entry.writeUInt16LE(1, 4) // planes
  entry.writeUInt16LE(32, 6) // bit count
  entry.writeUInt32LE(png.length, 8)
  entry.writeUInt32LE(22, 12)
  fs.writeFileSync(icoFile, Buffer.concat([header, entry, png]))
}

console.log('[single] 1/4 building portable folder…')
run(process.execPath, [path.join(root, 'scripts', 'package.mjs')])

fs.mkdirSync(workDir, { recursive: true })
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'))
const buildStamp = `${pkg.version}-${Date.now().toString(36)}`
const buildStampFile = path.join(workDir, 'BuildStamp.cs')
fs.writeFileSync(buildStampFile, `internal static class BuildStamp { public const string Id = "${buildStamp}"; }
`)
const assemblyInfo = path.join(workDir, 'AssemblyInfo.cs')
fs.writeFileSync(assemblyInfo, `using System.Reflection;
[assembly: AssemblyTitle("Aurora Launcher")]
[assembly: AssemblyDescription("Aurora Launcher")]
[assembly: AssemblyProduct("Aurora Launcher")]
[assembly: AssemblyCompany("Aurora Launcher")]
[assembly: AssemblyCopyright("Aurora Launcher")]
[assembly: AssemblyFileVersion("${pkg.version}")]
[assembly: AssemblyInformationalVersion("${pkg.version}")]
`)
const iconPng = path.join(root, 'resources', 'icon.png')
const iconIco = path.join(root, 'resources', 'icon.ico')
if (!fs.existsSync(iconIco)) makeIco(iconPng, iconIco)

console.log('[single] 2/4 compiling bootstrapper…')
const stub = path.join(workDir, 'AuroraLauncher.exe')
run(csc, [
  '-nologo',
  '-target:winexe',
  '-optimize+',
  `-win32icon:${iconIco}`,
  `-out:${stub}`,
  '-r:System.dll',
  '-r:System.Core.dll',
  '-r:System.Windows.Forms.dll',
  '-r:System.Drawing.dll',
  '-r:System.IO.Compression.dll',
  '-r:System.IO.Compression.FileSystem.dll',
  path.join(root, 'scripts', 'sfx', 'Stub.cs'),
  buildStampFile,
  assemblyInfo
])

console.log('[single] 3/4 compressing payload…')
const payload = path.join(workDir, 'payload.zip')
if (fs.existsSync(payload)) fs.rmSync(payload, { force: true })
const tar = 'C:\\Windows\\System32\\tar.exe'
run(tar, ['-a', '-c', '-f', payload, '-C', releaseDir, '.'])

console.log('[single] 4/4 appending payload…')
const stubBytes = fs.readFileSync(stub)
const zipBytes = fs.readFileSync(payload)
const footer = Buffer.alloc(16)
footer.writeBigUInt64LE(BigInt(zipBytes.length), 0)
Buffer.from('AURORASF', 'ascii').copy(footer, 8)
const output = path.join(root, 'release', 'AuroraLauncher.exe')
fs.writeFileSync(output, Buffer.concat([stubBytes, zipBytes, footer]))

const mb = (n) => `${(n / 1024 / 1024).toFixed(1)} MB`
console.log(`[single] done: ${output}`)
console.log(`[single] stub=${mb(stubBytes.length)} payload=${mb(zipBytes.length)} total=${mb(stubBytes.length + zipBytes.length)}`)
