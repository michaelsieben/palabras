import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

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
