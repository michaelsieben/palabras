#!/bin/bash
# Unraid only. Source of truth: deploy/unraid/docker-compose.yml in the checkout.
# Every rebuild mirrors that file to the docker share; .env is never overwritten.
# Run as root via User Scripts every five minutes. No webhook, PAT or volume deletion.
set -Eeuo pipefail
umask 077

readonly REPO=/mnt/user/repos/palabras
readonly DEPLOY=/mnt/user/docker/palabras
readonly LOG="$DEPLOY/deploy.log"
readonly STATE="$DEPLOY/deployed.sha"
mkdir -p "$DEPLOY"
exec 9>/var/lock/palabras-deploy.lock
flock -n 9 || { printf 'palabras: another deploy holds lock; skipped\n'; exit 0; }

# Rotate at 1 MiB; keep current file plus three historical files.
if [[ -f "$LOG" ]] && (( $(stat -c %s "$LOG") >= 1048576 )); then
    for i in 2 1; do
        [[ ! -f "$LOG.$i" ]] || mv -f "$LOG.$i" "$LOG.$((i + 1))"
    done
    mv -f "$LOG" "$LOG.1"
fi
exec > >(tee -a "$LOG") 2>&1
log() { printf '%s %s\n' "$(date -Is)" "$*"; }
trap 'rc=$?; log "ERROR deploy failed exit=$rc line=$LINENO; last healthy deployed.sha retained"; exit "$rc"' ERR
trap 'log "ERROR deploy interrupted; last healthy deployed.sha retained"; exit 130' INT TERM
fail() { log "ERROR $*"; exit 1; }

log 'poll started'
if [[ -e "$DEPLOY/paused" ]]; then
    log 'paused; no fetch or rebuild'
    exit 0
fi
[[ -d "$REPO/.git" ]] || fail 'checkout missing; add read-only deploy key in GitHub, then clone repository'
[[ -f "$DEPLOY/.env" ]] || fail 'deployment .env missing'
cd "$REPO"
[[ $(git symbolic-ref --short -q HEAD) == main ]] || fail 'checkout is not main; pause before rollback'
git diff --quiet && git diff --cached --quiet || fail 'tracked local edits; refusing to deploy'
# Refuse credential-bearing URLs so git output cannot accidentally disclose a PAT.
origin=$(git remote get-url origin)
case "$origin" in
    git@github-personal:michaelsieben/palabras.git) ;;
    *) fail 'unexpected origin URL; use github-personal SSH identity' ;;
esac
previous=none
[[ ! -f "$STATE" ]] || read -r previous < "$STATE"
log "previous healthy deployed sha=$previous"
git fetch --prune origin main
target=$(git rev-parse origin/main)
current=$(git rev-parse HEAD)
git merge-base --is-ancestor "$current" "$target" || fail 'origin/main is not a fast-forward; refusing reset/force'
if [[ "$current" != "$target" ]]; then
    git merge --ff-only origin/main
fi
health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' palabras 2>/dev/null || true)
if [[ "$previous" == "$target" && "$health" == healthy ]]; then
    cmp -s deploy/unraid/docker-compose.yml "$DEPLOY/docker-compose.yml" || fail 'compose mirror drift; restore versioned template or remove deployed.sha to rebuild; never edit mirror'
    log "no change sha=$target; healthy; no rebuild"
    exit 0
fi
log "deploying sha=$target (previous=$previous health=$health)"
[[ -f deploy/unraid/docker-compose.yml ]] || fail 'canonical compose missing'
cp deploy/unraid/docker-compose.yml "$DEPLOY/docker-compose.yml.next"
mv -f "$DEPLOY/docker-compose.yml.next" "$DEPLOY/docker-compose.yml"
docker compose --project-directory "$DEPLOY" -f "$DEPLOY/docker-compose.yml" config --quiet
docker compose --project-directory "$DEPLOY" -f "$DEPLOY/docker-compose.yml" up -d --build
for attempt in {1..36}; do
    health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' palabras 2>/dev/null || true)
    if [[ "$health" == healthy ]] && curl --fail --silent --show-error --max-time 5 http://127.0.0.1:18480/healthz; then
        printf '\n'
        printf '%s\n' "$target" > "$STATE.next"
        mv -f "$STATE.next" "$STATE"
        log "DEPLOY OK sha=$target healthy (previous=$previous)"
        exit 0
    fi
    log "health retry=$attempt/36 status=$health"
    sleep 5
done
docker logs --tail 30 palabras
fail "sha=$target did not become healthy after 36 attempts; inspect container; pause and rollback manually"
