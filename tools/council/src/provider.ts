import { setTimeout as delay } from 'node:timers/promises';
import type { CouncilConfig } from './config.js';
import { describePrivacyHit, scanForPrivateTerms } from './privacy.js';
import type { PrivacyPolicy } from './privacy.js';
import { httpProvider } from './providers/http.js';

export interface ProviderReply { text: string; inputTokens?: number; outputTokens?: number; cost?: number }
/** Adapters are supplied only to this chokepoint, never called by pipeline code. */
export type ProviderAdapter = (model: string, prompt: string, signal: AbortSignal) => Promise<ProviderReply>;
export class ProviderError extends Error {
  public readonly kind: 'auth' | 'http' | 'timeout' | 'network' | 'invalid' | 'config';
  public readonly status?: number;
  constructor(kind: ProviderError['kind'], status?: number) {
    super(`provider ${kind}${status === undefined ? '' : ` HTTP ${status}`}`);
    this.kind = kind;
    this.status = status;
  }
}
export class PrivacyBlockedError extends Error {}
export interface CouncilProvider { send(model: string, renderedPrompt: string): Promise<ProviderReply> }

export function assertPrivateTextSafe(policy: PrivacyPolicy, text: string, field: string): void {
  const hit = scanForPrivateTerms(policy, [{ field, text }])[0];
  if (hit) throw new PrivacyBlockedError(describePrivacyHit(hit));
}

export function createProvider(config: CouncilConfig, policy: PrivacyPolicy, adapter?: ProviderAdapter,
  sleep: (ms: number) => Promise<unknown> = delay): CouncilProvider {
  if (adapter === undefined) return httpProvider(config, policy, sleep);
  const transport = adapter;
  return {
    async send(model, renderedPrompt) {
      for (let attempt = 0; ; attempt++) {
        // Immediately before *every* adapter invocation, including retries and top-up.
        assertPrivateTextSafe(policy, renderedPrompt, 'rendered prompt');
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), config.timeoutSeconds * 1000);
        try {
          const reply = await Promise.race([
            transport(model, renderedPrompt, controller.signal),
            new Promise<never>((_resolve, reject) => controller.signal.addEventListener('abort',
              () => reject(new ProviderError('timeout')), { once: true })),
          ]);
          // Never persist dirty provider text, including malformed/unused proposals.
          assertPrivateTextSafe(policy, reply.text, 'provider output');
          return reply;
        } catch (error) {
          if (error instanceof PrivacyBlockedError) throw error;
          const safe = controller.signal.aborted ? new ProviderError('timeout') :
            error instanceof ProviderError ? error : new ProviderError('network');
          const retryable = safe.kind === 'timeout' || safe.status === 429 || (safe.status ?? 0) >= 500;
          if (attempt >= 2 || !retryable) throw safe;
          await sleep(1000 * 2 ** attempt);
        } finally { clearTimeout(timeout); }
      }
    },
  };
}
