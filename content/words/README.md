# content/words — seed batches 1–3

429 hand-authored A2-oriented entries, one JSON object per file, each named `<id>.json`. Batch 1 is
58 entries (34× A1, 24× A2); batch 2 is 171 entries (132× A1, 39× A2); batch 3 is 200 entries
(127× A1, 73× A2). Every entry is schema-valid and every entry is `status: "pending_review"` — none
is approved until the reviewer (the learner's fiancée, a native Peninsular speaker) approves it.

Validate with:

```bash
node tools/content-lint/index.js
```

Current result: `OK — 429 word file(s), 0 errors, 0 warning(s)`.

## What this batch covers

| Area | Topics | Count |
| --- | --- | --- |
| Household | nevera, lavadora, fregona, grifo, bombilla, enchufe, llave | 7 |
| Food and drink | patata, zumo, melocotón, fresa, tortilla, bocadillo, mercado, panadería | 8 |
| Weather | llover, nieve, calor, frío, paraguas, nublado | 6 |
| City and transport | coche, aparcar, autobús, acera, calle, plaza, móvil, ordenador, piso | 9 |
| Small talk and daily verbs | vale, apetecer, quedar, charlar, coger, andar, madrugar, desayunar | 8 |
| Family (in-laws included) | cuñado, sobrino, abuela, suegra, marido | 5 |
| Shopping and clothing | cartera, dinero, jersey, abrigo, zapatillas, gafas | 6 |
| Health | resfriado, fiebre, pastilla, médico, farmacia | 5 |
| Navarre culture | pincho, encierro | 2 |
| Time and meals | fin de semana, cena | 2 |

Design choices baked into the batch:

- Every word is usable with the fiancée's family in Pamplona: household objects, food shopping, bar
  life, weather, in-laws, health, small talk.
- Every entry carries **2–3 `alternatives`**, each with a `kind` (the reason: synonym, near-synonym,
  different-sense, regional, register), so the reviewer can reject and pick without authoring.
- Where a word is only Peninsular, the regional alternative is deliberately the LatAm form (`carro`,
  `computadora`, `jugo`, `celular`) so the reviewer sees exactly what she is vetoing.
- Where a word is correct in both varieties (`coger`, `tomar`, `apartamento`, `tú`/`vosotros`), the
  word stays and the contrast is put in `notes` / `registerNotes`, never dropped.
- Two entries carry two real senses on purpose: `piso` (flat / storey) and `zapatillas`
  (trainers / slippers). They are taught as separate senses, not merged.

## Deliberately deferred

- **Grammar spine words** (`ser`, `estar`, `tener`, `hacer`, `ir`, `haber`) — those belong to a
  grammar layer, not a vocabulary batch; their only value here would be a conjugation exercise.
- **Vosotros conjugation drills** — the rule is in `content/style-guide.md` (§2.1); teaching the
  forms is a grammar task, not a word entry.
- **Numbers, days, months, colours, body parts** — high-frequency A1 sets that deserve their own
  themed batch rather than being sprinkled here.
- **Vulgar and strongly colloquial material** — no `register: "vulgar"` entries exist (see §3 of the
  style guide); colloquial entries (`vale`, `pasta`, `finde`, `guay` as an alternative) are capped.
- **B1+ abstractions and idiom** — kept out on purpose; this is an everyday-use batch.
- **Proper nouns** (San Fermín, Calle Estafeta, Plaza del Castillo) appear inside examples but are
  not entries.
- **Pre-generated LatAm entries** as taught words — regional forms only appear as `regional`
  alternatives.

## Where the reviewer should look hardest

1. **In-law graph** — `cuñado`, `suegra`, `sobrino`, `marido`: are the `different-sense` alternatives
   the ones a Navarre family would actually use? Corrections here change several entries at once.
2. **Register of `vale`, `pasta`, `coger`, `pillar` (alternative)** — colloquialism is intentional, but
   the fiancée decides whether each is safe in front of her parents.
3. **`fregona`, `pincho`, `encierro`, `ambulatorio` (inside an example)** — Peninsular/Navarre-specific;
   confirm these are the words used at home, not just understood.
4. **False-friend triangles** — `resfriado`/`constipado`/`gripe`, `cartera` (wallet vs LatAm handbag),
   `tortilla` (Spanish omelette vs LatAm flatbread): confirm the taught sense is the household one.
5. **`zapatillas` dual sense and `piso` dual sense** — confirm both senses are wanted, or reject one.
6. **`bochorno`, `encapotado`, `facultativo`, `destemplanza`, `tahona`** — these appear only as
   alternatives, never as taught words; they are the most likely to be judged too advanced.
7. **Every `notes` field** — authoring uncertainty is recorded there by design; a `notes` line is a
   question to the reviewer, not a decision.

## Rules this batch was written under

`content/style-guide.md` (human reasoning + denylist/allowlist tables) and
`content/style-guide.lexicon.json` (the machine-readable lexicon the linter reads). If a rule changes,
edit both in one change and re-run the linter.

## Batch 2 (T-20261018-2105)

171 further entries, written under the same rules, filling the themed sets batch 1 deliberately
deferred (numbers were still left out; the grammar spine stays out). Every entry is
`status: "pending_review"`, `provenance.method: "hand-authored"`, with 2–3 `alternatives`.

| Area | Topics | Count |
| --- | --- | --- |
| Time: days, months, time adverbs | time, daily-routine | 28 |
| Family (extended, in-laws) | family | 17 |
| Body parts | health, daily-routine | 10 |
| Food and drink | food, shopping, navarre-culture | 18 |
| Clothing | clothing, weather, shopping, time | 10 |
| Household | household, food, daily-routine | 16 |
| Weather | weather | 8 |
| City and transport | city, transport, shopping, leisure | 14 |
| Technology | technology, small-talk, leisure | 8 |
| Health | health, navarre-culture | 6 |
| Small talk | small-talk | 8 |
| Daily-routine verbs | daily-routine, food, city, transport, leisure | 10 |
| Shopping | shopping, clothing, technology | 6 |
| Leisure | leisure, transport | 8 |
| Navarre culture | navarre-culture, food | 4 |

New local/Navarre entries: `chupinazo`, `sanfermines`, `txistorra`, `pacharán`, `ambulatorio`
(health centre), and `queso`/`iglesia`/`ayuntamiento`/`barrio` examples anchored in Pamplona.

Where the reviewer should look hardest:

1. **Cross-part-of-speech headwords** — `mañana` is taught only as the noun *morning*; the adverb
   *tomorrow* is a `different-sense` alternative, not a second entry. Confirm that split.
2. **`tío`/`tía`** — the family sense is taught; the colloquial Peninsular "bloke/mate" sense is kept
   out (only a `register` alternative points at `colega`).
3. **`txistorra` vs `chistorra`** — Basque vs Castilian spelling of the same Navarre sausage;
   `txistorra` is the headword because that is what Navarre menus print.
4. **`ambulatorio` vs `centro de salud`** — `ambulatorio` is the everyday Navarre word, `centro de
   salud` the official name; only `ambulatorio` is taught.
5. **`agua`** — taught as `article: "el"` with `gender: "feminine"`, matching el agua / el agua fría.
6. **`pescado` vs `pez`, `biblioteca` vs `librería`, `bolso` vs `bolsa`, `manzana` (fruit vs block)** —
   the false-friend pairs; confirm the taught sense is the household one.

## Batch 3 (T-20261018-2105 step 34)

200 further entries, same rules and schema. Written to broaden everyday coverage rather than to add
advanced material: no B1+ exotica, no numbers, the grammar spine stays out. Every entry is
`status: "pending_review"`, `provenance.method: "hand-authored"`, with 2–3 `alternatives`. CEFR mix:
127× A1, 73× A2.

Topic spread across the 200 new entries (an entry carries 1–3 topics, so the counts sum above 200):

| Topic | Entries |
| --- | --- |
| food | 34 |
| time | 25 |
| household | 22 |
| leisure | 22 |
| daily-routine | 21 |
| transport | 19 |
| small-talk | 18 |
| city | 17 |
| weather | 17 |
| health | 16 |
| technology | 14 |
| clothing | 13 |
| family | 13 |
| shopping | 13 |
| navarre-culture | 9 |

New local/Navarre entries: `pañuelico`, `peña`, `txapela`, `carnaval`, `churros`, plus Navarre-anchored
examples for `jamón`, `chorizo`, `aceituna`, `pimiento`, `sopa`, `parque` (Taconera), `museo` (Museo de
Navarra), `equipo` (Osasuna).

Where the reviewer should look hardest:

1. **`el hambre`** — feminine noun taught with `article: "el"`, like `agua` (`el hambre`, `mucha
   hambre`); confirm the article convention is what the family expects to see.
2. **`churros`, `peña`, `txapela`** — Navarre/Basque forms; confirm they are the words used at home.
3. **Cross-sense pairs** — `comida` (food / midday meal), `piso`-style splits are not repeated here;
   `partido` (match / political party) and `puente` (bridge / long weekend) are the ones to check.
4. **Register alternatives** — `colega`, `yayo`, `bici` sit in `alternatives` only, never as taught
   words; the batch itself is 100 % `neutral`.
