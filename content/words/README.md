# content/words — seed batch 1

58 hand-authored A2-oriented entries (34× A1, 24× A2), one JSON object per file, each named
`<id>.json`. Every entry is schema-valid and every entry is `status: "pending_review"` — none is
approved until the reviewer (the learner's fiancée, a native Peninsular speaker) approves it.

Validate with:

```bash
node tools/content-lint/index.js
```

Current result: `OK — 58 word file(s), 0 errors, 0 warning(s)`.

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
