import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { CouncilConfig } from './config.js';
import type { CouncilRequest } from './bundle.js';

export const PLACEHOLDERS = ['REQUEST', 'STYLE_GUIDE', 'CANDIDATE_SCHEMA', 'EXCLUSION_LIST', 'CANDIDATES'] as const;
export type PromptValues = Record<typeof PLACEHOLDERS[number], string>;
export class PromptError extends Error {}

/** Single pass: inserted values are never interpreted as templates. */
export function renderPrompt(template: string, values: PromptValues): string {
  const result = template.replace(/\{\{([^{}]*)\}\}/g, (_match, name: string) => {
    if (!PLACEHOLDERS.includes(name as typeof PLACEHOLDERS[number])) {
      throw new PromptError('unknown prompt placeholder');
    }
    return values[name as keyof PromptValues];
  });
  if (result.includes('{{')) throw new PromptError('leftover prompt placeholder');
  return result;
}

export function promptValues(config: CouncilConfig, request: CouncilRequest, exclusion: string[]): PromptValues {
  const schema = JSON.parse(readFileSync(path.join(config.paths.schemaDir, 'council-candidate.schema.json'), 'utf8'));
  delete schema.properties.key;
  delete schema.properties.council;
  schema.required = schema.required.filter((key: string) => key !== 'key' && key !== 'council');
  schema.properties.alternatives.minItems = 0;
  schema.$defs.alternative.required = ['field', 'rationale'];
  delete schema.$defs.alternative.properties.support;
  delete schema.$defs.alternative.properties.models;
  return { REQUEST: JSON.stringify(request), STYLE_GUIDE: readFileSync(config.paths.styleGuide, 'utf8'),
    CANDIDATE_SCHEMA: JSON.stringify(schema), EXCLUSION_LIST: exclusion.join('\n'), CANDIDATES: '[]' };
}

export function readTemplate(config: CouncilConfig, phase: 'propose' | 'critique'): string {
  return readFileSync(path.join(config.paths.promptsDir, `${phase}.${config.promptVersion[phase]}.md`), 'utf8');
}
