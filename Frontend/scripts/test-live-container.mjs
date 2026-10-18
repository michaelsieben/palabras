// Run from frontend host shell, not inside Playwright image (needs dotnet + Docker socket).
import assert from 'node:assert/strict'
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'

const repo = fileURLToPath(new URL('../../', import.meta.url))
const lock = JSON.parse(await readFile(`${repo}/Frontend/package-lock.json`, 'utf8'))
const version = lock.packages['node_modules/playwright'].version
const image = `mcr.microsoft.com/playwright:v${version}-noble`
// Docker bind paths are daemon-host paths, not this container's /repos paths.
const hostRepo = process.env.S10_HOST_REPO ?? '/mnt/user/repos/palabras'
const networkContainer = process.env.S10_NETWORK_CONTAINER ?? 'opencode'
const scratch = await mkdtemp('/tmp/palabras-s10-')
const password = randomUUID()
const env = { ...process.env, S10_SCRATCH: scratch, S10_PASSWORD: password,
  DATA_DIR: `${scratch}/data`, ConnectionStrings__Default: `Data Source=${scratch}/data/palabras.db`,
  DataProtection__KeysPath: `${scratch}/data/keys`, Logging__LogLevel__Default: 'Warning' }
let backend
function run(command, args, extraEnv = env) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: repo, env: extraEnv, stdio: 'inherit' })
    child.on('error', reject)
    child.on('exit', (code, signal) => code === 0 ? resolve() : reject(new Error(`${command} exited ${code ?? signal}`)))
  })
}
try {
  // Refuse to accidentally reuse an existing test/production listener.
  let occupied = false
  try { await fetch('http://127.0.0.1:5080/api/auth/me', { signal: AbortSignal.timeout(1000) }); occupied = true } catch { /* no listener */ }
  assert.equal(occupied, false, 'Port 5080 occupied; stop only your own test server before rerunning')
  console.log(`Live runner: ${image}; scratch ${scratch}; network container:${networkContainer}; bind ${hostRepo}; uid 99:100`)
  await run('docker', ['pull', image])
  await cp(`${repo}/Backend`, `${scratch}/Backend`, { recursive: true })
  await cp(`${repo}/content`, `${scratch}/content`, { recursive: true })
  await run('dotnet', ['publish', `${scratch}/Backend/Palabras/Palabras.csproj`, '-o', `${scratch}/app`])
  await run(process.execPath, [`${repo}/Frontend/tests/seedLiveBrowser.mjs`])
  backend = spawn('dotnet', [`${scratch}/app/Palabras.dll`, '--urls', 'http://0.0.0.0:5080'], { cwd: repo, env, stdio: 'inherit' })
  let ready = false
  for (let tries = 0; tries < 100; tries++) {
    assert.equal(backend.exitCode, null, 'Temporary backend exited before readiness')
    try {
      const response = await fetch('http://127.0.0.1:5080/api/auth/me', { signal: AbortSignal.timeout(1000) })
      if (response.status === 401) { ready = true; break }
    } catch { /* startup readiness only */ }
    await delay(100)
  }
  assert.ok(ready, 'Temporary backend did not return /api/auth/me 401')
  await run('docker', ['run', '--rm', '--name', `palabras-s10-live-${process.pid}`, '--user', '99:100',
    '--network', `container:${networkContainer}`, '-e', 'HOME=/tmp', '-e', `S10_PASSWORD=${password}`,
    '-v', `${hostRepo}:/repo`, '-w', '/repo/Frontend', image, 'node', 'tests/liveBrowser.mjs'])
} finally {
  if (backend && backend.exitCode === null) {
    const exited = new Promise(resolve => backend.once('exit', resolve))
    backend.kill('SIGTERM')
    await exited
  }
  await rm(scratch, { recursive: true, force: true })
  console.log('Temporary backend stopped; isolated scratch database/build/keys removed. Production untouched.')
}
