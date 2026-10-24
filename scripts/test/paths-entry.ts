import fs from 'node:fs'
import path from 'node:path'
import { defaultRootDir, hasMinecraftData } from '../../src/main/core/paths'

const detected = defaultRootDir()
console.log('detected root:', detected)
const expected = path.join(process.env.APPDATA ?? '', '.minecraft')
console.log('expected APPDATA .minecraft:', expected)
console.log('match:', path.resolve(detected) === path.resolve(expected))
console.log('has data:', hasMinecraftData(detected))
if (path.resolve(detected) !== path.resolve(expected) || !hasMinecraftData(detected)) process.exitCode = 1
const versions = path.join(detected, 'versions')
if (fs.existsSync(versions)) {
  console.log('versions:', fs.readdirSync(versions).filter((name) => fs.statSync(path.join(versions, name)).isDirectory()))
}
