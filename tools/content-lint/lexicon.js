'use strict';

/**
 * Peninsular-Spanish lexicon helpers, shared by the word linter (index.js) and the council
 * bundle linter (candidates.js).
 *
 * Extracted from index.js so the council candidate text is judged by the same rules as the
 * hand-authored corpus: one denylist, one tokeniser, one accent policy. Zero dependencies,
 * no network.
 */

const fs = require('fs');

/** lower-case, accent-sensitive: "papa" stays distinct from "papá". */
function norm(text) {
  return String(text).toLowerCase();
}

/** lower-case, accent-folded ascii: "móvil" -> "movil". Used for allowlist ids. */
function fold(text) {
  return norm(text).normalize('NFD').replace(/[\u0300-\u036f]/g, '');
}

/** Unicode-aware word tokens, punctuation dropped, accents kept. */
function tokens(text) {
  return norm(text)
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean);
}

function containsTerm(text, term) {
  const termTokens = tokens(term);
  if (termTokens.length === 0) return false;
  const textTokens = tokens(text);
  for (let i = 0; i + termTokens.length <= textTokens.length; i += 1) {
    let hit = true;
    for (let j = 0; j < termTokens.length; j += 1) {
      if (textTokens[i + j] !== termTokens[j]) { hit = false; break; }
    }
    if (hit) return true;
  }
  return false;
}

function countWords(text) {
  return tokens(text).length;
}

function loadLexicon(file) {
  const lexicon = JSON.parse(fs.readFileSync(file, 'utf8'));
  return {
    denylist: Array.isArray(lexicon.latamDenylist) ? lexicon.latamDenylist : [],
    allowlist: Array.isArray(lexicon.peninsularAllowlist) ? lexicon.peninsularAllowlist : [],
  };
}

module.exports = { norm, fold, tokens, containsTerm, countWords, loadLexicon };
