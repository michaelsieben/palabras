import test from 'node:test'
import assert from 'node:assert/strict'
import { parseBuildIdentity } from '../src/buildIdentity.ts'

const identity = { version: '0.7.0', commit: '0303a87f0d3c4e4f0a5b9c8d7e6f5a4b3c2d1e0f', commitShort: '0303a87', milestone: 'M7' }

test('the /healthz identity is read as served', () => {
  assert.deepEqual(parseBuildIdentity({ status: 'ok', ...identity }), identity)
})

test('a build that was not told its commit stays visibly unknown', () => {
  // The server degrades to the literal "unknown"; the settings screen must show that string rather
  // than an empty gap or an invented short sha.
  const unknown = { ...identity, commit: 'unknown', commitShort: 'unknown' }
  assert.deepEqual(parseBuildIdentity(unknown), unknown)
})

test('a body without the identity fields does not render a half-identity', () => {
  assert.equal(parseBuildIdentity({ status: 'ok', version: '0.7.0' }), null)
  assert.equal(parseBuildIdentity({ ...identity, milestone: '' }), null)
  assert.equal(parseBuildIdentity({ ...identity, commit: 42 }), null)
  assert.equal(parseBuildIdentity(null), null)
  assert.equal(parseBuildIdentity('ok'), null)
})
