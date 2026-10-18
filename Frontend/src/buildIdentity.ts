// The build identity the server reports on /healthz. Shapes only: nothing is inferred, so a build
// that was never told its commit shows the literal "unknown" instead of a plausible-looking sha.
export type BuildIdentity = { version: string; commit: string; commitShort: string; milestone: string }

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null
}

export function parseBuildIdentity(body: unknown): BuildIdentity | null {
  if (typeof body !== 'object' || body === null) return null
  const record = body as Record<string, unknown>
  const version = text(record.version), commit = text(record.commit)
  const commitShort = text(record.commitShort), milestone = text(record.milestone)
  if (!version || !commit || !commitShort || !milestone) return null
  return { version, commit, commitShort, milestone }
}
