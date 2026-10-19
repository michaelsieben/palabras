# Palabras content style guide — Peninsular/Castilian Spanish, A2, one learner

This guide is the authoring contract for every file in `content/words/`. It has two readers:

- the **linter** `tools/content-lint/index.js`, which enforces the machine-checkable parts and reads the
  lexicon in [`style-guide.lexicon.json`](./style-guide.lexicon.json);
- the **human reviewer** (the learner's fiancée, a native speaker, final authority on every word), who
  applies the judgement the linter cannot.

Target variety: **European Spanish as spoken in Pamplona, Navarre** (`es-ES`). Latin American Spanish
(`es-419`) is never the taught form. Where a word is correct in both varieties it is kept and the sense
is labelled — it is never dropped.

Corpus imports are approved by default (except explicit drafts); the reviewer queue is a place to object,
not a prerequisite for study. Rejecting suspends cards and keeps history; re-approving restores cards fresh
unless they are manually or leech-paused. Council drafts remain review-gated. The app itself must run with
**zero LLM dependency**: nothing in this directory is generated at runtime, and no example, gloss or
alternative may require a network call.

---

## 1. Variant rule and how it is enforced

Two layers:

1. **Linter (hard, exit 1).** For every entry it checks
   - the JSON schema in `content/schema/word.schema.json`;
   - filename must equal `<id>.json`;
   - no duplicate ids, no duplicate sense (headword + gloss);
   - the headword and (for `matchIn` rules) the example sentences are scanned against
     `style-guide.lexicon.json` → `latamDenylist`;
   - example sentences: ≤12 words and ≤140 chars, terminal punctuation present, `¿`/`¡` paired with `?`/`!`;
   - soft warning when `ustedes`/`usted` appears in an example of a non-`formal` entry.
2. **Reviewer (soft, final).** Leísmo, `pretérito perfecto` preference, verb conjugations, whether a
   colloquial word is safe in front of the fiancée's family, and whether a word is truly A2.

### 1.1 The denylist (reject these forms)

Left column is the Latin American form the linter rejects; right column is the Peninsular form to teach.
Each rejection reason is stored per entry in `style-guide.lexicon.json` and repeated here so the reviewer
argued the same way twice.

| Rejected (es-419) | Teach instead | Note |
| --- | --- | --- |
| `carro`, `auto` | `coche` | car. Headword-only match: `carro` = cart in Spain. |
| `computadora`, `computador` | `ordenador` | computer. |
| `celular` | `móvil` | mobile phone. |
| `jugo` | `zumo` | juice. `jugo` in Spain means a body fluid. |
| `durazno` | `melocotón` | peach. |
| `frutilla` | `fresa` | strawberry. |
| `palta` | `aguacate` | avocado. |
| `billetera` | `cartera` | wallet. |
| `lapicero`, `esfero`, `pluma` (pen sense) | `bolígrafo` | pen. `pluma` = feather in Spain. |
| `colectivo`, `guagua`, `micro`, `camión` (bus sense) | `autobús` | bus. `camión` = truck in Spain. |
| `estacionar`, `parquear` | `aparcar` | to park. |
| `departamento` | `piso` | flat. `apartamento` is shared and acceptable. |
| `suéter`, `sweater` | `jersey` | jumper. |
| `heladera`, `refrigerador` | `nevera` (or `frigorífico`) | fridge. |
| `manejar` (drive sense) | `conducir` | to drive. `manejar` = to handle in Spain. |
| `pastel` (cake sense) | `tarta` | cake. `pastel` can be a savoury pie in Spain. |
| `vereda`, `banqueta` (sidewalk sense) | `acera` | pavement. |
| `bombillo`, `foco` (bulb sense) | `bombilla` | light bulb. `foco` = spotlight in Spain. |
| `tomacorriente`, `toma corriente` | `enchufe` | socket/plug. |
| `ahorita` | `ahora` | never Peninsular. |
| `platicar` | `charlar` | to chat. |
| `playeras`, `tenis` (trainers sense) | `zapatillas` | trainers. `tenis` = the sport in Spain. |
| `lentes`, `anteojos` | `gafas` | glasses. |
| `banana`, `banano` | `plátano` | banana. |
| `ananá` | `piña` | pineapple. |
| `papa` (potato sense) | `patata` | potato. `papa` = Pope; `papá` = dad. |
| `lindo` | `bonito` | pretty; `lindo` reads es-419/literary here. |

### 1.2 The allowlist (endorse these forms)

Allowlisting is not decorative: it is the review-facing record of every word form this project has
already decided is Peninsular, and the linter suppresses a headword-only denylist hit for a form that is
explicitly allowlisted (for example `camión` if it were ever taught as *truck*). The full list with a
reason per term is `style-guide.lexicon.json` → `peninsularAllowlist`. It includes the task-mandated set
(`coche`, `ordenador`, `patata`, `nevera`, `móvil`, `zumo`, `melocotón`, `fresa`, `conducir`, `coger`,
`tarta`, `piso`, `jersey`, `cartera`, `bolígrafo`, `autobús`, `aparcar`, `vale`) plus every seed word
whose Peninsular status is settled.

### 1.3 Words correct in both varieties

Do not drop them and do not flag them. Keep the word, and label the **sense** in `senses[].notes` as
shared, and put the other variety's different word in `alternatives` with `kind: "regional"`. Examples in
the seed batch: `coger` (everyday in Spain, vulgar in parts of LatAm), `tú`/`vosotros` (shared concept,
different plural form), `apartamento` (shared) vs `piso` (Peninsular), `tomar` (shared synonym of
`coger el autobús` and `beber`).

### 1.4 `coger` and its LatAm trap

`coger` is fully correct, everyday Peninsular Spanish for *to take / to catch*. It is **allowed**. Its
LatAm vulgar meaning does not apply to this household in Pamplona. Because it is a known trap, an entry
that teaches it must carry `registerNotes` saying it is everyday in Spain. `coger el autobús` is an
allowlisted Peninsular collocation.

---

## 2. Address, verbs and grammar register

### 2.1 `tú` over `usted`; `vosotros` as the plural

- Singular: teach **`tú`**. `usted` is taught only inside a `formal` entry or when the example is
  explicitly with an older/unknown person. The linter warns when `usted`/`ustedes` shows up in a
  non-`formal` example.
- Plural: teach **`vosotros`** with its own forms (`vosotros tenéis`, `vosotros vais`, `os`). Never teach
  `ustedes` as the everyday plural; `ustedes` is Peninsular only in formal/peninsular-south contexts and
  lives behind `register: "formal"`. Conjugate every verb example accordingly (`¿Vosotros queréis…?`,
  not `¿Ustedes quieren…?`).
- Never teach `vos`/voseo.

### 2.2 `pretérito perfecto` for today's events

Peninsular usage prefers the compound perfect for events inside today's or the current period:
`Hoy he desayunado tarde`, `Esta mañana he ido al mercado`, not `Hoy desayuné tarde`. Simple past
(`pretérito indefinido`) is for closed past periods (`ayer fui`, `el año pasado viajé`) and for
storytelling. This is a **human-review rule** — the linter does not guess tenses. Reasons to reject an
example: a `pretérito indefinido` with `hoy`/`esta mañana`/`esta semana`, or `pretérito perfecto` with
`ayer`/`el mes pasado`.

### 2.3 Leísmo

Avoid leísmo: use `lo`/`la`/`los`/`las` for direct objects, even for people (`A mi cuñado lo veo el
domingo`), and keep `le` for indirect objects (`Le he dado el pan a mi suegra`). Leísmo of person is
tolerated in casual Peninsular speech, but the learner should learn the standard distinction; teach `lo`
and `la` and note `le` only if a specific nearby usage needs it. Human-review rule.

### 2.4 `c`/`z` and the /θ/ sound

Teach and spell the **distinción**: `c` before `e`/`i` and `z` before `a`/`o`/`u` → /θ/ (`cena`,
`cocina`, `zumo`, `plaza`), never the seseo spelling `seso`/`sena`. Correct spelling is a hard
requirement for TTS: the Pamplona voice pronounces /θ/ and reads the written `c`/`z` normally. Never
write a word with `s` where Peninsular spelling uses `c`/`z` (a word like `cocina` is never `cosina`).
`ñ` is mandatory (`melocotón`, `piña`), as are `á é í ó ú`.

---

## 3. Register rules

`register` is one of `neutral | colloquial | informal | formal | vulgar | humorous | technical`.

- Default is `neutral`. Most seed entries are `neutral`.
- `colloquial` and `informal` are allowed and wanted (`vale`, `guay`, `finde`, `pasta` = money); the
  learner marries into a family and needs warm, real speech.
- Cap per batch: **≤ ~15% `colloquial`/`informal`**, and never two colloquial entries that are the same
  joke.
- `vulgar` is excluded by default. It may only be added if the learner's fiancée explicitly asks, with a
  non-empty `registerNotes` stating who it is safe with. The seed batch contains none.
- `formal` is allowed when the sense is genuinely formal (`usted` contexts, paperwork).
- `humorous` needs `registerNotes`.

---

## 4. Example sentences, glosses and tone

- **One sense = one gloss = one example.** Never merge meanings. If the same headword has two real
  meanings, use two `senses[]` items, each with its own example. `alternatives` of
  `kind: "different-sense"` point at the *other* meaning.
- **Length:** at most **12 words** and 140 characters per example. One clause where possible. A2 reading
  load: present, perfect, simple past, `ir a` + infinitive, `me gusta`, `hay`, polite requests with
  `¿me pones…?`.
- **Vocabulary:** an A2 example may use A1 function words freely, but not B1+ abstractions. No subordinate
  clauses chained more than once.
- **Punctuation, Peninsular orthography:** examples end in `.`, `?` or `!`. Questions and exclamations
  open with `¿`/`¡` (`¿Me pones un vaso de agua?`, `¡Qué rico!`).
- **Locale truth:** the example must be something a Pamplona speaker would actually say. Bars, `pinchos`,
  `la compra`, walking to the centre on foot, cold mornings, Sunday family lunch: that is the register of
  this corpus. Avoid invented textbook dialogues.
- **English glosses:** lowercase, plain, British English (`jumper`, `flat`, `pavement`, `trainers`,
  `trousers`) to match the Peninsular terms. One to six words. `exampleGloss` translates the example
  sentence naturally, not word for word.
- **Tone:** warm, direct, first person, address the learner as `tú`. No infantilising language ("good
  job!", "super easy!"): he is an adult learning his partner's language. No exclamation in the gloss
  field; keep the Spanish example's exclamations.
- **Uncertainty:** if you cannot justify that a word is Peninsular, or that it is A2, keep it out of the
  taught sense and put the doubt in `notes` (schema field). An entry whose `notes` says "shared with
  es-419" is more valuable than one that silently guesses.

---

## 5. Alternatives (the no-LLM escape hatch)

Every entry carries **2–4 pre-generated alternatives** so the reviewer can reject a word and pick a
replacement without authoring anything, and without the running app calling a model. Each alternative has
a `kind` (the *reason*) and a `reason` string:

- `synonym` — same meaning, Peninsular, safe swap (`frigorífico` for `nevera`).
- `near-synonym` — close but not identical (`charlar` vs `hablar`); the `reason` states the difference.
- `different-sense` — the other real meaning of the headword (`copa` vs `vaso`; `plaza` vs `glorieta`).
- `regional` — another variety's word. Leaning LatAm on purpose so the reviewer can see and veto it
  (`carro`, `computadora`, `jugo`). These are the *only* place LatAm forms are allowed, and they are
  never taught as the variant.
- `register` — same meaning, different formality (`vale` → `de acuerdo`; `dinero` → `pasta`).

When the reviewer rejects an entry, the app offers these `alternatives` as the pick list. Choosing one
either promotes an existing entry (via `targetId`) or creates a replacement entry from the same bundle;
either way no model runs at decision time.

---

## 6. Making this enforceable

- Machine-checkable today: schema, id/filename, uniqueness, denylist, example length, punctuation,
  `usted`/`ustedes` warning.
- Human-only, listed here so the reviewer knows what the linter ignored: leísmo, `pretérito perfecto`,
  `tú`/`vosotros` correctness in conjugation, colloquialism safety, A2 level, cultural fit for Pamplona.
- When the two disagree, the reviewer wins; if a rule needs to change, edit this file and
  `style-guide.lexicon.json` in the same change, then re-run `node tools/content-lint/index.js`.
