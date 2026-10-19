import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { REPO_ROOT, TOOL_ROOT } from './config.js';
import { describePrivacyHit, loadPrivacyPolicy, PrivacyConfigError, scanForPrivateTerms } from './privacy.js';
import type { PrivacyScanInput } from './privacy.js';

/** Hash-only CI boundary. Never loads a local exclusion file. A valid empty list is allowed. */
export function checkPrivacyHashes(hashFile: string, inputs: PrivacyScanInput[]): number {
  const policy = loadPrivacyPolicy({ hashFile, requireHashes: true });
  const hits = scanForPrivateTerms(policy, inputs);
  if (hits.length) throw new PrivacyConfigError(hits.map(describePrivacyHit).join('\n'));
  return policy.hashedTerms.length;
}

function textFiles(directory: string): PrivacyScanInput[] {
  return readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name)).flatMap((entry) => {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) return textFiles(file);
    if (!entry.isFile() || !/\.(json|jsonl|md)$/.test(entry.name)) return [];
    return [{ field: path.relative(REPO_ROOT, file), text: readFileSync(file, 'utf8') }];
  });
}

const entry = process.argv[1];
if (entry !== undefined && path.resolve(entry) === fileURLToPath(import.meta.url)) {
  try {
    const hashFile = path.join(TOOL_ROOT, 'privacy.sha256.json');
    // Check configuration before reading content, including deliberate initial empty state.
    checkPrivacyHashes(hashFile, []);
    const inputs = [...textFiles(path.join(REPO_ROOT, 'content')), ...textFiles(path.join(TOOL_ROOT, 'prompts'))];
    const count = checkPrivacyHashes(hashFile, inputs);
    if (count === 0) {
      console.log('privacy hash guard NOTICE: committed hash list is empty — defence-in-depth only; a real run still requires non-empty privacy.local.json');
    } else {
      console.log(`privacy hash guard OK: hashed_terms=${count} files=${inputs.length}`);
    }
  } catch (error) {
    const message = error instanceof PrivacyConfigError
      ? error.message : 'privacy hash guard failed: content scan could not complete';
    console.error(message);
    process.exitCode = 1;
  }
}
