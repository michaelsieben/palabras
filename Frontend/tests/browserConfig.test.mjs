import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import test from 'node:test'

function config(port) {
  const env = { ...process.env }
  if (port === undefined) delete env.PALABRAS_TEST_PORT
  else env.PALABRAS_TEST_PORT = port
  return spawnSync(process.execPath, ['--input-type=module', '-e', `
    import { testPort, testBaseUrl } from ${JSON.stringify(new URL('./browserConfig.mjs', import.meta.url).href)};
    console.log(JSON.stringify({ testPort, testBaseUrl }));
  `], { env, encoding: 'utf8' })
}

test('browser preview port and assertion origin share default and env override', () => {
  for (const [input, port] of [[undefined, 4173], ['4183', 4183], ['65535', 65535]]) {
    const result = config(input)
    assert.equal(result.status, 0, result.stderr)
    assert.deepEqual(JSON.parse(result.stdout), { testPort: port, testBaseUrl: `http://127.0.0.1:${port}` })
  }
})

test('browser port rejects invalid overrides instead of falling back to unrelated origin', () => {
  for (const port of ['', '0', '-1', '65536', '4173.5', 'wrong']) {
    const result = config(port)
    assert.notEqual(result.status, 0)
    assert.match(result.stderr, /PALABRAS_TEST_PORT must be an integer between 1 and 65535/)
  }
})
