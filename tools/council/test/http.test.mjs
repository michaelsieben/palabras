import assert from 'node:assert/strict';
import test from 'node:test';
import { createProvider } from '../dist/provider.js';
import { workspace } from './helpers.mjs';

for (const vendor of ['openrouter', 'openai', 'anthropic', 'google', 'mistral']) {
  test(`${vendor} HTTP transport uses private gateway, correct envelope, bounded metrics`, async () => {
    const w = workspace();
    const original = globalThis.fetch;
    try {
      const config = { ...w.config, env: { OPENROUTER_API_KEY: 'synthetic-key', OPENAI_API_KEY: 'synthetic-key',
        ANTHROPIC_API_KEY: 'synthetic-key', GOOGLE_GENERATIVE_AI_API_KEY: 'synthetic-key', MISTRAL_API_KEY: 'synthetic-key' } };
      let calls = 0;
      globalThis.fetch = async (_url, init) => {
        calls++;
        const request = JSON.parse(init.body);
        if (vendor === 'anthropic') {
          assert.equal(request.messages[0].content, 'clean prompt');
          assert.equal(init.headers['x-api-key'], 'synthetic-key');
          return Response.json({ content: [{ type: 'text', text: '{}' }], usage: { input_tokens: 10, output_tokens: 3 } });
        }
        if (vendor === 'google') {
          assert.equal(request.contents[0].parts[0].text, 'clean prompt');
          assert.equal(init.headers['x-goog-api-key'], 'synthetic-key');
          return Response.json({ candidates: [{ content: { parts: [{ text: '{}' }] } }], usageMetadata: { promptTokenCount: 10, candidatesTokenCount: 3 } });
        }
        assert.equal(request.messages[0].content, 'clean prompt');
        assert.equal(init.headers.Authorization, 'Bearer synthetic-key');
        return Response.json({ choices: [{ message: { content: '{}' } }], usage: { prompt_tokens: 10, completion_tokens: 3 } });
      };
      const reply = await createProvider(config, w.policy).send(`${vendor}:fixture`, 'clean prompt');
      assert.equal(calls, 1);
      assert.deepEqual(reply, { text: '{}', inputTokens: 10, outputTokens: 3 });
    } finally { globalThis.fetch = original; w.cleanup(); }
  });
}

test('HTTP failure does not read or surface response body; absent key never calls fetch', async () => {
  const w = workspace();
  const original = globalThis.fetch;
  try {
    let calls = 0;
    globalThis.fetch = async () => {
      calls++;
      return new Response('UNSAFE response detail', { status: 401 });
    };
    await assert.rejects(createProvider({ ...w.config, env: { OPENAI_API_KEY: 'synthetic-key' } }, w.policy).send('openai:fixture', 'clean'), /provider auth HTTP 401/);
    assert.equal(calls, 1);
    await assert.rejects(createProvider(w.config, w.policy).send('openai:fixture', 'clean'), /provider auth/);
    assert.equal(calls, 1);
  } finally { globalThis.fetch = original; w.cleanup(); }
});

test('timeout aborts even non-cooperative adapter and retries only twice', async () => {
  const w = workspace();
  try {
    let calls = 0;
    const provider = createProvider({ ...w.config, timeoutSeconds: 0.001 }, w.policy, async () => {
      calls++;
      return new Promise(() => {});
    }, async () => {});
    await assert.rejects(provider.send('openai:fixture', 'clean'), /provider timeout/);
    assert.equal(calls, 3);
  } finally { w.cleanup(); }
});
