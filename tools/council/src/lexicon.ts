import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { REPO_ROOT } from './config.js';

/**
 * The committed corpus as a lexicon (Q-M3-10): `content/words/*.json` is the source of the
 * exclusion list a propose run is given, and of the word keys a bundle's `target_key` may point
 * at. The CLI reads the files instead of the database on purpose - it is an offline tool and must
 * never need app credentials, a running server or a SQLite file.
 *
 * A file the corpus linter would reject is skipped here rather than repaired: `content-lint` owns
 * reporting broken content, and a lexicon is not the place to guess a missing lemma.
 */

export interface CorpusEntry {
  key: string;
  lemma: string;
  pos: string;
}

export interface CorpusLexicon {
  directory: string;
  entries: CorpusEntry[];
  keys: Set<string>;
  /** Files that could not supply id+lemma+pos. Not an error: content-lint reports those. */
  skipped: number;
}

export function loadCorpusLexicon(directory: string): CorpusLexicon {
  if (!existsSync(directory)) {
    throw new Error(`lexicon directory not found: ${directory}`);
  }
  const entries: CorpusEntry[] = [];
  const keys = new Set<string>();
  let skipped = 0;
  const files = readdirSync(directory)
    .filter((file) => file.endsWith('.json'))
    .sort();
  for (const file of files) {
    let data: unknown;
    try {
      data = JSON.parse(readFileSync(path.join(directory, file), 'utf8'));
    } catch {
      skipped += 1;
      continue;
    }
    // The corpus files use the content schema's names (`headword`, `partOfSpeech`); the council
    // vocabulary says `lemma`, `pos`. One reader, both dialects, no duplicated key list.
    const word = data as { id?: unknown; lemma?: unknown; headword?: unknown; pos?: unknown; partOfSpeech?: unknown };
    const lemma = typeof word.lemma === 'string' ? word.lemma : word.headword;
    const pos = typeof word.pos === 'string' ? word.pos : word.partOfSpeech;
    if (typeof word.id !== 'string' || typeof lemma !== 'string' || typeof pos !== 'string') {
      skipped += 1;
      continue;
    }
    keys.add(word.id);
    entries.push({ key: word.id, lemma, pos });
  }
  return { directory, entries, keys, skipped };
}

/** `lemma|pos`, the pair the exclusion list is built from (design ΔE step 1). */
export function exclusionList(entries: CorpusEntry[]): string[] {
  const seen = new Set<string>();
  const pairs: string[] = [];
  for (const entry of entries) {
    const pair = `${entry.lemma}|${entry.pos}`;
    if (!seen.has(pair)) {
      seen.add(pair);
      pairs.push(pair);
    }
  }
  return pairs;
}

/** Occupied keys are additive: an override never hides the committed corpus or bundles. */
export function loadProducerLexicon(override?: string, councilDir = path.join(REPO_ROOT, 'content/council')): CorpusLexicon {
  const corpusDir = path.join(REPO_ROOT, 'content/words');
  const result = loadCorpusLexicon(corpusDir);
  if (override !== undefined && path.resolve(override) !== corpusDir) {
    const extra = loadCorpusLexicon(override);
    result.entries.push(...extra.entries);
    for (const key of extra.keys) result.keys.add(key);
    result.skipped += extra.skipped;
  }
  if (existsSync(councilDir)) {
    for (const dir of readdirSync(councilDir, { withFileTypes: true }).filter((entry) => entry.isDirectory()).sort((a,b) => a.name.localeCompare(b.name))) {
      const file = path.join(councilDir, dir.name, 'candidates.jsonl');
      if (!existsSync(file)) continue;
      // Corrupt committed bundles must not silently free an occupied key.
      for (const line of readFileSync(file, 'utf8').split('\n').filter((text) => text.trim())) {
        let word: CorpusEntry;
        try { word = JSON.parse(line); } catch { throw new Error('committed council lexicon is not valid JSONL'); }
        if (typeof word.key !== 'string' || typeof word.lemma !== 'string' || typeof word.pos !== 'string') {
          throw new Error('committed council lexicon lacks key, lemma or pos');
        }
        result.keys.add(word.key);
        result.entries.push(word);
      }
    }
  }
  return result;
}
