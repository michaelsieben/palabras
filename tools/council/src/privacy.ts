import { existsSync, readFileSync } from 'node:fs';
import { sha256Hex } from './bundle.js';

/**
 * Privacy guard (design ΔH).
 *
 * Two lists, both normalised the same way (NFC, lower-case, accent-folded, whitespace collapsed):
 *
 * - `privacy.local.json` (gitignored) holds the strings that must never leave the machine: real
 *   names, usernames, email, the domain. It is scanned by plain normalised substring, because a
 *   name or an address can sit inside a token ("ana@example" is not two words).
 * - `privacy.sha256.json` (committed) holds only SHA-256 hashes, so CI and the lint can run the
 *   same guard without the strings entering git. A hash cannot be substring-matched, so each entry
 *   records its token count and the scan hashes every n-gram of that width.
 *
 * A hit aborts the caller before any network call, and the diagnostic names the *field* that is
 * dirty, never the term: printing the term would leak exactly what the guard protects.
 */

const MIN_TERM_LENGTH = 3;
const MAX_HASHED_TOKENS = 8;

export interface HashedTerm {
  tokens: number;
  sha256: string;
}

export interface PrivacyPolicy {
  localTerms: string[];
  hashedTerms: HashedTerm[];
  localSource: string | null;
  hashedSource: string | null;
}

export interface PrivacyLoadOptions {
  localFile?: string;
  hashFile?: string;
  /** An explicitly requested file that does not exist is a configuration error, not an empty list. */
  requireLocal?: boolean;
  requireHashes?: boolean;
}

export interface PrivacyScanInput {
  /** Human-readable name of where the text came from, e.g. "content/style-guide.md". */
  field: string;
  text: string;
}

export interface PrivacyHit {
  field: string;
  source: 'local' | 'hashed';
  /** 1-based position in that list; the only detail printed about the term itself. */
  index: number;
}

export class PrivacyConfigError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'PrivacyConfigError';
  }
}

export function normaliseTerm(text: string): string {
  return text
    .normalize('NFC')
    .toLowerCase()
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

export function termHash(normalisedText: string): string {
  return sha256Hex(normalisedText);
}

export function tokenise(normalisedText: string): string[] {
  return normalisedText.split(/[^\p{L}\p{N}@._-]+/u).filter((token) => token !== '');
}

function readJson(file: string): unknown {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    throw new PrivacyConfigError(`${file} is not valid JSON: ${(error as Error).message}`);
  }
}

function readLocalTerms(file: string): string[] {
  const document = readJson(file);
  const raw = Array.isArray(document)
    ? document
    : (document as { terms?: unknown }).terms;
  if (!Array.isArray(raw)) {
    throw new PrivacyConfigError(`${file} must be an object with a "terms" array (or a bare array of strings)`);
  }
  const terms: string[] = [];
  for (const [index, value] of raw.entries()) {
    if (typeof value !== 'string') {
      throw new PrivacyConfigError(`${file}: terms[${index}] is not a string`);
    }
    const normalised = normaliseTerm(value);
    if (normalised.length === 0) {
      continue;
    }
    if (normalised.length < MIN_TERM_LENGTH) {
      // A one- or two-character term would match almost every prompt while protecting nothing.
      // Do not echo the term: the file is the secret.
      throw new PrivacyConfigError(
        `${file}: terms[${index}] is shorter than ${MIN_TERM_LENGTH} characters after normalisation; a term that short cannot be a name, address or domain`
      );
    }
    terms.push(normalised);
  }
  return terms;
}

function readHashedTerms(file: string): HashedTerm[] {
  const document = readJson(file) as { algorithm?: unknown; terms?: unknown };
  if (document.algorithm !== undefined && document.algorithm !== 'sha256') {
    throw new PrivacyConfigError(`${file}: algorithm must be "sha256", got ${JSON.stringify(document.algorithm)}`);
  }
  if (!Array.isArray(document.terms)) {
    throw new PrivacyConfigError(`${file} must be an object with a "terms" array of {tokens, sha256} entries`);
  }
  const terms: HashedTerm[] = [];
  for (const [index, value] of document.terms.entries()) {
    const entry = value as { tokens?: unknown; sha256?: unknown };
    const tokens = entry.tokens;
    const sha256 = entry.sha256;
    if (typeof tokens !== 'number' || !Number.isInteger(tokens) || tokens < 1 || tokens > MAX_HASHED_TOKENS) {
      throw new PrivacyConfigError(`${file}: terms[${index}].tokens must be an integer 1-${MAX_HASHED_TOKENS}`);
    }
    if (typeof sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(sha256)) {
      throw new PrivacyConfigError(`${file}: terms[${index}].sha256 must be 64 lower-case hex characters`);
    }
    terms.push({ tokens, sha256 });
  }
  return terms;
}

export function loadPrivacyPolicy(options: PrivacyLoadOptions = {}): PrivacyPolicy {
  let localTerms: string[] = [];
  let hashedTerms: HashedTerm[] = [];
  const localSource = options.localFile ?? null;
  const hashedSource = options.hashFile ?? null;

  if (localSource !== null) {
    if (existsSync(localSource)) {
      localTerms = readLocalTerms(localSource);
    } else if (options.requireLocal === true) {
      throw new PrivacyConfigError(`privacy list not found: ${localSource}`);
    }
  }
  if (hashedSource !== null) {
    if (existsSync(hashedSource)) {
      hashedTerms = readHashedTerms(hashedSource);
    } else if (options.requireHashes === true) {
      throw new PrivacyConfigError(`privacy hash list not found: ${hashedSource}`);
    }
  }
  return { localTerms, hashedTerms, localSource, hashedSource };
}

/**
 * Scans every text that may become part of an outgoing prompt. Returns one hit per (field, term)
 * and never the term itself, so the caller can abort without echoing it.
 */
export function scanForPrivateTerms(policy: PrivacyPolicy, inputs: PrivacyScanInput[]): PrivacyHit[] {
  const hits: PrivacyHit[] = [];
  for (const input of inputs) {
    const normalised = normaliseTerm(input.text);
    if (normalised === '') {
      continue;
    }
    for (const [index, term] of policy.localTerms.entries()) {
      if (normalised.includes(term)) {
        hits.push({ field: input.field, source: 'local', index: index + 1 });
      }
    }
    if (policy.hashedTerms.length === 0) {
      continue;
    }
    const tokens = tokenise(normalised);
    for (const [index, term] of policy.hashedTerms.entries()) {
      let found = false;
      for (let start = 0; start + term.tokens <= tokens.length && !found; start += 1) {
        const window = tokens.slice(start, start + term.tokens).join(' ');
        if (termHash(window) === term.sha256) {
          found = true;
        }
        // An address or a domain with a separator stays one token, so a single-token entry also
        // has to be compared against each token rather than only against joined windows.
        if (!found && term.tokens === 1 && termHash(tokens[start] as string) === term.sha256) {
          found = true;
        }
      }
      if (found) {
        hits.push({ field: input.field, source: 'hashed', index: index + 1 });
      }
    }
  }
  return hits;
}

export function describePrivacyHit(hit: PrivacyHit): string {
  return `${hit.field} contains a private term (${hit.source} #${hit.index})`;
}
