import assert from 'node:assert/strict'
import { reviewWord, approvedWord } from './reviewFixtures.mjs'

export function reviewBrowserFixture(options = {}) {
  const fixture = {
    pending: options.pending ?? Array.from({ length: 30 }, (_, i) => reviewWord(i + 1)),
    approved: options.approved ?? Array.from({ length: 35 }, (_, i) => approvedWord(i + 101)),
    rejected: options.rejected ?? [],
    sent: [], reads: [], results: new Map(), failRequests: false, failQueue: false, staleCursor: false,
    outcome: undefined,
    async fulfill(route) {
      const request = route.request(); const url = new URL(request.url())
      if (url.pathname.endsWith('/queue')) {
        fixture.reads.push(url.pathname + url.search)
        const status = url.searchParams.get('status')
        assert.ok(['pending', 'approved', 'rejected', 'all'].includes(status), 'queue must send explicit status')
        if (fixture.failQueue) return route.abort('failed')
        let items = status === 'rejected' ? fixture.rejected : fixture.pending
        const after = url.searchParams.get('after')
        if (after) {
          const index = items.findIndex(w => `${status === 'rejected' || status === 'approved' ? 'd' : 'p'}:${w.wordId}` === after)
          if (fixture.staleCursor || index < 0) { fixture.staleCursor = false; return route.fulfill({ status: 400, json: { error: 'bad_request' } }) }
          items = items.slice(index + 1)
        }
        const page = items.slice(0, 30)
        return route.fulfill({ json: { items: page, pendingTotal: status === 'rejected' ? 0 : fixture.pending.length, topics: [], rejectedTotal: status === 'rejected' ? fixture.rejected.length : 0, nextAfter: status !== 'pending' && items.length > 30 ? `${status === 'rejected' || status === 'approved' ? 'd' : 'p'}:${page.at(-1).wordId}` : null } })
      }
      if (url.pathname.endsWith('/approved')) {
        fixture.reads.push(url.search)
        assert.equal(url.searchParams.get('status'), 'approved')
        const q = (url.searchParams.get('q') ?? '').toLowerCase()
        let items = fixture.approved.filter(w => `${w.headword} ${w.gloss}`.toLowerCase().includes(q))
        const after = url.searchParams.get('after')
        if (after) {
          const index = items.findIndex(w => String(w.wordId) === after)
          if (fixture.staleCursor || index < 0) { fixture.staleCursor = false; return route.fulfill({ status: 400, json: { error: 'bad_request' } }) }
          items = items.slice(index + 1)
        }
        return route.fulfill({ json: { items: items.slice(0, Number(url.searchParams.get('limit') ?? 30)) } })
      }
      assert.equal(url.pathname, '/api/review/actions')
      assert.equal(request.headers()['x-palabras'], '1')
      const actions = request.postDataJSON().actions
      fixture.sent.push(structuredClone(actions))
      if (fixture.failRequests) return route.fulfill({ status: 500, json: { error: 'corrupt' } })
      const results = actions.map(a => {
        if (fixture.results.has(a.clientReviewId)) return { ...fixture.results.get(a.clientReviewId), result: 'duplicate' }
        const result = fixture.outcome?.(a) ?? 'applied'
        const r = { clientReviewId: a.clientReviewId, result }
        if (result === 'applied') {
          fixture.results.set(a.clientReviewId, r)
          if (a.action === 'demote') fixture.approved = fixture.approved.filter(w => w.wordId !== a.wordId)
          else if (a.action === 'reject') {
            const word = fixture.approved.find(w => w.wordId === a.wordId) ?? fixture.pending.find(w => w.wordId === a.wordId)
            fixture.rejected.unshift(reviewWord(a.wordId, { ...word, status: 'rejected' }))
            fixture.approved = fixture.approved.filter(w => w.wordId !== a.wordId)
            fixture.pending = fixture.pending.filter(w => w.wordId !== a.wordId)
          } else {
            const word = fixture.rejected.find(w => w.wordId === a.wordId)
            if (word) fixture.approved.unshift(approvedWord(a.wordId, { ...word, gloss: word.senses[0].gloss, status: 'approved' }))
            fixture.rejected = fixture.rejected.filter(w => w.wordId !== a.wordId)
            fixture.pending = fixture.pending.filter(w => w.wordId !== a.wordId)
          }
          r.word = { status: a.action === 'demote' ? 'pending_review' : a.action === 'reject' ? 'rejected' : 'approved', version: a.wordVersion }
        }
        return r
      })
      return route.fulfill({ json: { results } })
    },
  }
  return fixture
}
