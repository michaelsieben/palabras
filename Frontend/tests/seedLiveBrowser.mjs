// Test-only fixture setup. Never points at /data or a production database.
import assert from 'node:assert/strict'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'

const scratch = process.env.S10_SCRATCH
assert.ok(scratch?.startsWith('/tmp/palabras-s10-'), 'S10_SCRATCH must be isolated /tmp/palabras-s10-*')
const password = process.env.S10_PASSWORD
assert.ok(password?.length >= 12, 'Set throwaway S10_PASSWORD, never production credentials')
const env = { ...process.env, DATA_DIR: `${scratch}/data`, ConnectionStrings__Default: `Data Source=${scratch}/data/palabras.db`, DataProtection__KeysPath: `${scratch}/data/keys` }
await mkdir(`${scratch}/fixtures`, { recursive: true })
function cli(args, input) {
  const result = spawnSync('dotnet', [`${scratch}/app/Palabras.dll`, ...args], { env, input, encoding: 'utf8' })
  process.stdout.write(result.stdout ?? ''); process.stderr.write(result.stderr ?? '')
  assert.equal(result.status, 0, `CLI ${args.join(' ')} failed`)
}
cli(['user', 'create', 'owner', '--username', 'learner', '--password-stdin'], `${password}\n`)
cli(['user', 'create', 'reviewer', '--username', 'reviewer', '--password-stdin'], `${password}\n`)
const word = JSON.parse(await readFile(new URL('../../content/words/es-autobus-01.json', import.meta.url), 'utf8'))
// No exact headword in example, so server emits typed rather than alternating cloze.
word.senses[0].example = 'Voy al centro en transporte público.'
word.senses[0].exampleGloss = 'I go to the centre by public transport.'
await writeFile(`${scratch}/fixtures/es-autobus-01.json`, JSON.stringify(word))
cli(['import', `${scratch}/fixtures`])
cli(['content', 'approve', 'es-autobus-01'])
const db = new DatabaseSync(`${scratch}/data/palabras.db`)
try {
  db.exec('PRAGMA foreign_keys=ON')
  const recognition = db.prepare("SELECT c.*, s.word_id FROM cards c JOIN senses s ON s.id=c.sense_id WHERE c.direction='es_en'").get()
  const typed = db.prepare("SELECT * FROM cards WHERE direction='en_es'").get()
  assert.ok(recognition && typed)
  const now = Date.now(); const earlier = now - 2 * 86_400_000; const future = now + 30 * 86_400_000
  // Explicit test history unlocks EN→ES via genuine production predicate; no mocked plan.
  db.prepare('UPDATE cards SET state=2, stability=2, difficulty=5, reps=1, last_review_ms=?, due_ms=? WHERE id=?').run(earlier, future, recognition.id)
  db.prepare('UPDATE cards SET state=2, stability=2, difficulty=5, reps=0, last_review_ms=?, due_ms=? WHERE id=?').run(earlier, now - 1000, typed.id)
  db.prepare(`INSERT INTO review_log
    (client_attempt_id,user_id,word_id,card_id,card_epoch,rating,reviewed_at_ms,received_at_ms,
     elapsed_days,interval_after_days,mode,state_before,state_after,due_after_ms,weights_version,counts_for_srs)
    VALUES (?,?,?,?,?,3,?,?,0,30,'recognition',0,2,?,0,1)`)
    .run(randomUUID(), recognition.user_id, recognition.word_id, recognition.id, recognition.epoch, earlier, earlier, future)
  console.log(`Live fixture ready: one due typed card ${typed.id}; historical ES→EN unlock; temporary DB ${scratch}/data/palabras.db`)
} finally { db.close() }
