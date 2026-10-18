#!/bin/bash
# Unraid only. Source of truth: deploy/unraid/docker-compose.yml in the checkout.
# Every rebuild mirrors that file to the docker share; .env is never overwritten.
# Run as root via User Scripts every five minutes. No webhook, PAT or volume deletion.
# Data guards (T-20261018-1547): the pre-rebuild database check, the pre-rebuild backup and
# the post-deploy account check all run before deployed.sha is written, so a deployment that
# lost its accounts or its database fails loudly instead of being recorded as healthy.
set -Eeuo pipefail
umask 077

readonly REPO=/mnt/user/repos/palabras
# Trust only this checkout, including when root polls a tree owned by another uid.
export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.directory GIT_CONFIG_VALUE_0="$REPO"
readonly DEPLOY=/mnt/user/docker/palabras
readonly LOG="$DEPLOY/deploy.log"
readonly STATE="$DEPLOY/deployed.sha"
# Data backups live outside the data directory, in the deployment directory only.
readonly BACKUP_DIR="$DEPLOY/backups"
readonly BACKUP_KEEP=5
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
git_checked() {
    local rc
    if git "$@"; then
        return 0
    else
        rc=$?
        # ">&2" is load-bearing: git_checked is also called inside a command substitution
        # (branch=$(git_checked ...)), where stdout is captured into the variable. The message
        # must go to stderr to stay visible, and the non-zero return is what fails the caller.
        fail "git $1 failed exit=$rc" >&2
    fi
}
check_clean() {
    local rc
    if git diff "$@" --quiet; then
        return 0
    else
        rc=$?
        if (( rc == 1 )); then
            fail 'tracked local edits; refusing to deploy'
        fi
        fail "git diff failed exit=$rc"
    fi
}

# --- Data-integrity guards (T-20261018-1547) ---------------------------------
# The bind source for /data is read from the deployed .env, because compose interpolates
# APPDATA_PATH from it. It is never hardcoded here, so a share or path change cannot
# silently disable these guards.
env_appdata_path() {
    local line key value=
    while IFS= read -r line || [[ -n "$line" ]]; do
        line=${line%$'\r'}
        line=${line#"${line%%[![:space:]]*}"}
        [[ "$line" == *=* ]] || continue
        key=${line%%=*}
        key=${key%"${key##*[![:space:]]}"}
        [[ "$key" == APPDATA_PATH ]] || continue
        # Compose tolerates spaces around "=" and trims the value; accept the same shapes here.
        value=${line#*=}
        value=${value#"${value%%[![:space:]]*}"}
        value=${value%"${value##*[![:space:]]}"}
        if [[ "$value" == \"*\" || "$value" == \'*\' ]]; then
            value=${value:1:${#value}-2}
        fi
    done < "$DEPLOY/.env"
    printf '%s\n' "$value"
}

# Guard 1: before anything is fetched, mirrored or rebuilt, the deployment's data directory
# must still hold a database. The app creates a fresh empty database when /data is empty,
# which is how the live deployment ended up with zero accounts. Never create or repair data
# here: abort and leave deployed.sha alone. Only enforced once a healthy sha is recorded, so
# a first boot (no deployed.sha yet) can still provision.
require_deployed_data() {
    local context=$1 size header
    if [[ ! -f "$STATE" ]]; then
        log "WARN data guard ($context): no $STATE yet, so this is a first deploy; $db is expected to be absent"
        return 0
    fi
    if [[ -L "$db" ]]; then
        fail "data guard FAILED ($context): $db is a symlink; refusing to deploy and not writing $STATE (the data path must be the real file, so a moved or redirected path is visible here instead of being followed silently)"
    fi
    if [[ ! -e "$db" ]]; then
        fail "data guard FAILED ($context): $db is missing; refusing to deploy and not writing $STATE. Likely cause: the Unraid mover moved appdata off the cache pool because the appdata share cache mode is not Only/Prefer, or APPDATA_PATH in $DEPLOY/.env is wrong. The app would start on an empty /data and recreate a database with ZERO accounts. Restore the data deliberately and pause the poll; this script never recreates data."
    fi
    if [[ ! -f "$db" ]]; then
        fail "data guard FAILED ($context): $db is not a regular file; refusing to deploy and not writing $STATE"
    fi
    if ! size=$(stat -c %s "$db"); then
        fail "data guard FAILED ($context): cannot read the size of $db; refusing to deploy and not writing $STATE"
    fi
    if (( size == 0 )); then
        fail "data guard FAILED ($context): $db is 0 bytes (empty); refusing to deploy and not writing $STATE. Likely cause: the Unraid mover moved appdata off the cache pool because the appdata share cache mode is not Only/Prefer, or APPDATA_PATH in $DEPLOY/.env is wrong. An empty file is not a usable database; restore deliberately, this script never recreates data."
    fi
    if ! header=$(head -c 15 "$db" 2>/dev/null); then
        header=
    fi
    if [[ "$header" != 'SQLite format 3' ]]; then
        log "WARN data guard ($context): $db has no SQLite header; continuing, but treat the database as suspect"
    fi
    log "data guard ok ($context): $db size=$size"
}

# Guard 2: copy the database (with its WAL/SHM sidecars) and the cookie keys to a directory
# outside the data path before the rebuild. A backup failure aborts the deploy; it is never
# ignored. Copies never dereference symlinks, and pruning only ever removes this script's own
# timestamped directories inside $BACKUP_DIR.
backup_data() {
    local stamp dir suffix
    if [[ ! -d "$BACKUP_DIR" ]] && ! mkdir -p "$BACKUP_DIR"; then
        fail "backup FAILED: cannot create $BACKUP_DIR; refusing to deploy"
    fi
    if [[ ! -e "$db" ]]; then
        log "backup skipped: $db absent, nothing to copy"
        return 0
    fi
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    dir="$BACKUP_DIR/$stamp-${target:0:7}"
    suffix=1
    while [[ -e "$dir" ]]; do
        # A second deploy of the same sha inside the same second must not reuse or overwrite
        # an existing snapshot.
        dir="$BACKUP_DIR/$stamp-${target:0:7}-$suffix"
        suffix=$((suffix + 1))
    done
    if ! mkdir "$dir"; then
        fail "backup FAILED: cannot create $dir; refusing to deploy"
    fi
    if ! cp -a -- "$db" "$dir/palabras.db"; then
        fail "backup FAILED: cannot copy $db to $dir/palabras.db; refusing to deploy"
    fi
    for suffix in -wal -shm; do
        if [[ -e "$db$suffix" ]]; then
            if ! cp -a -- "$db$suffix" "$dir/palabras.db$suffix"; then
                fail "backup FAILED: cannot copy $db$suffix; refusing to deploy"
            fi
        fi
    done
    if [[ -d "$data_dir/keys" ]]; then
        if ! cp -a -- "$data_dir/keys" "$dir/keys"; then
            fail "backup FAILED: cannot copy $data_dir/keys; refusing to deploy"
        fi
    else
        log "WARN backup: no keys directory at $data_dir/keys; session keys will not be recoverable from this backup"
    fi
    if [[ ! -s "$dir/palabras.db" ]]; then
        fail "backup FAILED: $dir/palabras.db is empty; refusing to deploy"
    fi
    log "backup ok: $dir"
    prune_backups
}

prune_backups() {
    local entry
    while IFS= read -r entry; do
        # Only this script's own backup directories, only inside $BACKUP_DIR, never symlinks.
        case "$entry" in
            [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T[0-9][0-9][0-9][0-9][0-9][0-9]Z-*) ;;
            *) continue ;;
        esac
        if [[ ! -d "$BACKUP_DIR/$entry" || -L "$BACKUP_DIR/$entry" ]]; then
            continue
        fi
        if ! rm -rf -- "$BACKUP_DIR/$entry"; then
            fail "backup prune failed: cannot remove $BACKUP_DIR/$entry"
        fi
        log "backup prune: removed $BACKUP_DIR/$entry"
    done < <(ls -1 -- "$BACKUP_DIR" 2>/dev/null | grep -E '^[0-9]{8}T[0-9]{6}Z-' | LC_ALL=C sort -r | tail -n "+$((BACKUP_KEEP + 1))")
}

# Guard 3: ask the app's own CLI inside the container how many accounts the deployment has.
# Sets ACCOUNTS_STATE to count|cli-unavailable|no-table and ACCOUNTS_COUNT when it is a count.
accounts_probe() {
    local out rc
    ACCOUNTS_STATE=cli-unavailable ACCOUNTS_COUNT= ACCOUNTS_RC= ACCOUNTS_RAW=
    if out=$(docker exec palabras dotnet /app/Palabras.dll user list 2>&1); then
        :
    else
        rc=$?
        ACCOUNTS_RC=$rc ACCOUNTS_RAW=$out
        return 1
    fi
    if ! grep -qE '^ROLE[[:space:]]' <<< "$out"; then
        ACCOUNTS_STATE=no-table ACCOUNTS_RAW=$out
        return 1
    fi
    ACCOUNTS_STATE=count ACCOUNTS_RAW=$out
    ACCOUNTS_COUNT=$(grep -cE '^(owner|reviewer)[[:space:]]' <<< "$out" || true)
    return 0
}

# Zero accounts is a failed deploy, not a successful one: data was lost. An uninvocable CLI is
# a distinct failure, never a pass. Nothing is restored, created or reset here.
require_accounts() {
    local context=$1
    if ! accounts_probe; then
        log "account check ($context) raw output: $ACCOUNTS_RAW"
        if [[ "$ACCOUNTS_STATE" == cli-unavailable ]]; then
            fail "account check FAILED ($context): cannot invoke the accounts CLI in container palabras (docker exec /app/Palabras.dll user list exit=$ACCOUNTS_RC); accounts are UNVERIFIED, which is not a successful deploy; $STATE not written, previous deployed.sha retained; inspect the container and image"
        fi
        fail "account check FAILED ($context): accounts CLI output unrecognised (no ROLE table header); accounts are UNVERIFIED, which is not a successful deploy; $STATE not written, previous deployed.sha retained; inspect the container and image"
    fi
    log "account check ($context): accounts=$ACCOUNTS_COUNT"
    if (( ACCOUNTS_COUNT < 1 )); then
        if [[ ! -f "$STATE" ]]; then
            log "WARN account check ($context): ZERO accounts and no $STATE yet (first deploy); recording the first healthy sha so bootstrap can continue; create the owner account with: docker exec palabras dotnet /app/Palabras.dll user create owner --username <name> --password-stdin"
            return 0
        fi
        log "account check ($context) raw output: $ACCOUNTS_RAW"
        fail "account check FAILED ($context): ZERO accounts in container palabras, so the deployment lost its accounts (data directory $data_dir); $STATE not written, previous deployed.sha retained; inspect the data directory and pause the poll before any manual restore; this deploy is NOT recorded as good"
    fi
    if [[ -n "$pre_accounts" ]] && (( ACCOUNTS_COUNT < pre_accounts )); then
        fail "account check FAILED ($context): accounts dropped from $pre_accounts to $ACCOUNTS_COUNT in container palabras; accounts must survive deployments; $STATE not written, previous deployed.sha retained"
    fi
    return 0
}

log 'poll started'
if [[ -e "$DEPLOY/paused" ]]; then
    log 'paused; no fetch or rebuild'
    exit 0
fi
[[ -d "$REPO/.git" ]] || fail 'repository checkout missing; add read-only deploy key in GitHub, then clone repository'
[[ -f "$DEPLOY/.env" ]] || fail 'deployment .env missing'
# Data directory as deployed: compose interpolates APPDATA_PATH from this .env into the /data bind.
if ! data_dir=$(env_appdata_path); then
    fail "cannot read APPDATA_PATH from $DEPLOY/.env; cannot locate the data directory; refusing to deploy"
fi
[[ -n "$data_dir" ]] || fail 'APPDATA_PATH missing or empty in the deployed .env; cannot locate the data directory; refusing to deploy'
[[ "$data_dir" == /* ]] || data_dir="$DEPLOY/$data_dir"
db="$data_dir/palabras.db"
require_deployed_data 'poll'
cd "$REPO" || fail "cannot enter repository checkout $REPO; check that the path exists and is readable"
git_checked rev-parse --is-inside-work-tree > /dev/null
branch=$(git_checked rev-parse --abbrev-ref HEAD)
[[ "$branch" != HEAD ]] || fail 'checkout has detached HEAD; pause before rollback'
[[ "$branch" == main ]] || fail "checkout branch is $branch, expected main; pause before rollback"
check_clean
check_clean --cached
# Refuse credential-bearing URLs so git output cannot accidentally disclose a PAT.
if origin=$(git config --get remote.origin.url); then
    [[ -n "$origin" ]] || fail 'origin URL absent; configure github-personal SSH identity'
else
    rc=$?
    if (( rc == 1 )); then
        fail 'origin URL absent; configure github-personal SSH identity'
    fi
    fail "git config failed reading origin URL exit=$rc"
fi
if origin=$(git remote get-url origin); then
    :
else
    rc=$?
    fail "git remote failed reading origin URL exit=$rc"
fi
[[ -n "$origin" ]] || fail 'origin URL absent; configure github-personal SSH identity'
case "$origin" in
    git@github-personal:michaelsieben/palabras.git) ;;
    *) fail 'unexpected origin URL; use github-personal SSH identity' ;;
esac
previous=none
pre_accounts=
[[ ! -f "$STATE" ]] || read -r previous < "$STATE"
log "previous healthy deployed sha=$previous"
git_checked fetch --prune origin main
if target=$(git rev-parse origin/main); then
    :
else
    rc=$?
    fail "git rev-parse origin/main failed exit=$rc"
fi
if current=$(git rev-parse HEAD); then
    :
else
    rc=$?
    fail "git rev-parse HEAD failed exit=$rc"
fi
if git merge-base --is-ancestor "$current" "$target"; then
    :
else
    rc=$?
    if (( rc == 1 )); then
        fail 'origin/main is not a fast-forward; refusing reset/force'
    fi
    fail "git merge-base failed exit=$rc"
fi
if [[ "$current" != "$target" ]]; then
    git merge --ff-only origin/main
fi
health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' palabras 2>/dev/null || true)
if [[ "$previous" == "$target" && "$health" == healthy ]]; then
    cmp -s deploy/unraid/docker-compose.yml "$DEPLOY/docker-compose.yml" || fail 'compose mirror drift; restore versioned template or remove deployed.sha to rebuild; never edit mirror'
    # A healthy container with no accounts is exactly how data loss went unnoticed: /healthz
    # never touched the database. Refuse the green poll instead of reporting success.
    require_accounts 'no-change poll'
    log "no change sha=$target; healthy; no rebuild"
    exit 0
fi
log "deploying sha=$target (previous=$previous health=$health)"
[[ -f deploy/unraid/docker-compose.yml ]] || fail 'canonical compose missing'
# Best-effort account count before the rebuild, so a partial loss is visible too. The container
# may be absent or down here, which is not a failure.
if accounts_probe && [[ "$ACCOUNTS_STATE" == count ]]; then
    pre_accounts=$ACCOUNTS_COUNT
    log "pre-rebuild accounts count=$pre_accounts"
else
    pre_accounts=
    log "pre-rebuild accounts count unavailable (state=$ACCOUNTS_STATE)"
fi
backup_data
cp deploy/unraid/docker-compose.yml "$DEPLOY/docker-compose.yml.next"
mv -f "$DEPLOY/docker-compose.yml.next" "$DEPLOY/docker-compose.yml"
docker compose --project-directory "$DEPLOY" -f "$DEPLOY/docker-compose.yml" config --quiet
docker compose --project-directory "$DEPLOY" -f "$DEPLOY/docker-compose.yml" up -d --build
for attempt in {1..36}; do
    health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' palabras 2>/dev/null || true)
    if [[ "$health" == healthy ]] && curl --fail --silent --show-error --max-time 5 http://127.0.0.1:18480/healthz; then
        printf '\n'
        # Accounts are checked before the deploy is recorded as good; a failure here leaves the
        # previous deployed.sha in place so the bad state is visible and is never marked healthy.
        require_accounts "post-deploy sha=$target"
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
