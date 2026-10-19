// Internal transport. Only provider.ts may invoke this; it owns privacy and retries.
import type { CouncilConfig } from '../config.js';
import { ProviderError, createProvider } from '../provider.js';
import type { CouncilProvider, ProviderAdapter, ProviderReply } from '../provider.js';
import type { PrivacyPolicy } from '../privacy.js';

/** Raw HTTP transport is private; this module only exports a privacy-guarded sender. */
export function httpProvider(config: CouncilConfig, policy: PrivacyPolicy, sleep?: (ms: number) => Promise<unknown>): CouncilProvider {
  return createProvider(config, policy, httpAdapter(config), sleep);
}

function httpAdapter(config: CouncilConfig): ProviderAdapter {
  return async (id, prompt, signal) => {
    const colon = id.indexOf(':');
    const vendor = id.slice(0, colon);
    const model = id.slice(colon + 1);
    let url: string;
    let headers: Record<string, string> = { 'Content-Type': 'application/json' };
    let body: object;
    if (vendor === 'anthropic') {
      const key = config.env.ANTHROPIC_API_KEY;
      if (!key) throw new ProviderError('auth');
      url = 'https://api.anthropic.com/v1/messages';
      headers = { ...headers, 'x-api-key': key, 'anthropic-version': '2023-06-01' };
      body = { model, max_tokens: 32768, temperature: config.temperature, messages: [{ role: 'user', content: prompt }] };
    } else if (vendor === 'google') {
      const key = config.env.GOOGLE_GENERATIVE_AI_API_KEY;
      if (!key) throw new ProviderError('auth');
      url = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(model)}:generateContent`;
      headers['x-goog-api-key'] = key;
      body = { contents: [{ role: 'user', parts: [{ text: prompt }] }],
        generationConfig: { temperature: config.temperature, responseMimeType: 'application/json' } };
    } else {
      const routes: Record<string, [string, string]> = {
        openrouter: ['https://openrouter.ai/api/v1/chat/completions', 'OPENROUTER_API_KEY'],
        openai: ['https://api.openai.com/v1/chat/completions', 'OPENAI_API_KEY'],
        mistral: ['https://api.mistral.ai/v1/chat/completions', 'MISTRAL_API_KEY'],
      };
      const route = routes[vendor];
      if (!route) throw new ProviderError('config');
      const key = config.env[route[1]];
      if (!key) throw new ProviderError('auth');
      url = route[0];
      headers.Authorization = `Bearer ${key}`;
      body = { model, temperature: config.temperature, messages: [{ role: 'user', content: prompt }],
        response_format: { type: 'json_object' } };
    }
    const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify(body), signal });
    if (!response.ok) throw new ProviderError(response.status === 401 || response.status === 403 ? 'auth' : 'http', response.status);
    // Do not expose parser exceptions or provider response bodies in diagnostics.
    try {
      const data = await response.json() as Record<string, any>;
      let reply: ProviderReply;
      if (vendor === 'anthropic') {
        reply = { text: data.content?.filter((item: any) => item.type === 'text').map((item: any) => item.text).join('') };
        reply.inputTokens = data.usage?.input_tokens;
        reply.outputTokens = data.usage?.output_tokens;
      } else if (vendor === 'google') {
        reply = { text: data.candidates?.[0]?.content?.parts?.map((item: any) => item.text ?? '').join('') };
        reply.inputTokens = data.usageMetadata?.promptTokenCount;
        reply.outputTokens = data.usageMetadata?.candidatesTokenCount;
      } else {
        reply = { text: data.choices?.[0]?.message?.content, inputTokens: data.usage?.prompt_tokens, outputTokens: data.usage?.completion_tokens };
      }
      if (typeof reply.text !== 'string') throw new ProviderError('invalid');
      // Only bounded numeric metrics from providers can reach manifests.
      for (const key of ['inputTokens', 'outputTokens'] as const) {
        if (!Number.isSafeInteger(reply[key]) || (reply[key] ?? -1) < 0) delete reply[key];
      }
      return reply;
    } catch { throw new ProviderError('invalid'); }
  };
}
