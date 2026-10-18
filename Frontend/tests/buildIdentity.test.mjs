import test from 'node:test'
import assert from 'node:assert/strict'
import { randomBytes, randomInt } from 'node:crypto'
import { parseBuildIdentity } from '../src/buildIdentity.ts'

// The app's source is the serving /healthz response, not the frontend package version or a build
// define. Use fresh, release-shaped synthetic responses: no fixture needs updating on a release.
function servedIdentity() {
  const commit = randomBytes(20).toString('hex')
  return {
    version: Array.from({ length: 3 }, () => randomInt(1, 10000)).join('.'),
    commit,
    commitShort: commit.slice(0, 7),
    milestone: `M${randomInt(1, 10000)}`,
  }
}

test('the /healthz identity is read as served', () => {
  const identity = servedIdentity()
  assert.deepEqual(parseBuildIdentity({ status: 'ok', ...identity }), identity)
})

test('a build that was not told its commit stays visibly unknown', () => {
  // The server degrades to the literal "unknown"; the settings screen must show that string rather
  // than an empty gap or an invented short sha.
  const unknown = { ...servedIdentity(), commit: 'unknown', commitShort: 'unknown' }
  assert.deepEqual(parseBuildIdentity(unknown), unknown)
})

test('a body without the identity fields does not render a half-identity', () => {
  const identity = servedIdentity()
  assert.equal(parseBuildIdentity({ status: 'ok', version: identity.version }), null)
  for (const field of Object.keys(identity)) {
    const missing = { ...identity }
    delete missing[field]
    assert.equal(parseBuildIdentity(missing), null, `missing ${field}`)
    for (const invalid of ['', ' \t\n', null, undefined, 42, false, {}, []]) {
      assert.equal(parseBuildIdentity({ ...identity, [field]: invalid }), null, `invalid ${field}: ${JSON.stringify(invalid)}`)
    }
  }
  assert.equal(parseBuildIdentity(null), null)
  assert.equal(parseBuildIdentity('ok'), null)
})

test('a later response replaces every identity field without a frontend default', () => {
  const first = servedIdentity()
  // Guarantee distinct values rather than relying on random draws never colliding.
  const next = Object.fromEntries(Object.entries(first).map(([field, value]) => [field, `${value}-next`]))
  assert.deepEqual(parseBuildIdentity(first), first)
  assert.deepEqual(parseBuildIdentity({ status: 'ok', ...next, unrelated: 'ignored' }), next)
  // Missing server metadata is also served literally; the parser validates shape, not release policy.
  const unknown = Object.fromEntries(Object.keys(first).map(field => [field, 'unknown']))
  assert.deepEqual(parseBuildIdentity(unknown), unknown)
})
