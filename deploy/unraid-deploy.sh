#!/bin/bash
# Unraid only. Source of truth: deploy/unraid/docker-compose.yml in the checkout.
# Every rebuild mirrors that file to the docker share; .env is never overwritten.
# Run as root via User Scripts every five minutes. No webhook, PAT or volume deletion.
# Data guards (T-20261018-1547): the pre-rebuild database check, the pre-rebuild backup and
# the post-deploy account check all run before deployed.sha is written, so a deployment that
# lost its accounts or its database fails loudly instead of being recorded as healthy.
# Image identity (T-20261018-2105): the unchanged-deployment decision also compares the RUNNING
# container's image id with the image the recorded deployment produced (deployed.image), so a
# container left on an older build cannot be reported as current forever. Before a rebuild the
# running image is tagged deterministically (palabras:rollback-<image-id>), so a rollback target
# survives the rebuild instead of becoming an untagged dangling image.
# Build identity (T-20261018-2105): before a rebuild the sha of the checkout being built is
# exported as COMMIT_SHA, and deploy/unraid/docker-compose.yml interpolates it into the build
# args, so /healthz, Ajustes and the image label report which commit the image is. It is read
# from the checkout (git rev-parse HEAD), never from deployed.sha, which can name another
# commit. If it cannot be read the build still proceeds with "unknown": a build that lies about
# its identity is worse than one that admits it does not know it.
set -Eeuo pipefail
umask 077

readonly REPO=/mnt/user/repos/palabras
# Trust only this checkout, including when root polls a tree owned by another uid.
export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.directory GIT_CONFIG_VALUE_0="$REPO"
readonly DEPLOY=/mnt/user/docker/palabras
readonly LOG="$DEPLOY/deploy.log"
readonly STATE="$DEPLOY/deployed.sha"
# Image id the recorded deployment of that sha ran: "<image id> <sha>". Written only after a
# deploy is accepted, so the next poll can compare it with the container that is actually running.
readonly STATE_IMAGE="$DEPLOY/deployed.image"
# Manifest of preserved rollback tags, oldest first. Retention: newest ROLLBACK_KEEP tags kept.
readonly ROLLBACK_TAGS="$DEPLOY/rollback-images"
readonly ROLLBACK_KEEP=2
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

# --- Image identity and rollback-artifact preservation (T-20261018-2105) -----
# The unchanged-deployment decision must not rest on deployed.sha alone. A container can keep
# running an older image while the recorded sha already names the target, and then every poll
# logs "no change; healthy; no rebuild" while production drifts. So the running container's image
# id is compared with the image the recorded deployment of that sha produced (deployed.image);
# the tag the poll itself builds (palabras:local) is accepted only when no record exists for the
# target sha, and that weaker match is logged as such. A mismatch is "needs deploy", never a no-op.

# Running container image id. Sets CONTAINER_STATE=absent|image|error and CONTAINER_IMAGE.
# An absent container is a normal first-deploy state; a docker failure is not, and is an error.
container_image() {
    local names rc
    CONTAINER_STATE=error CONTAINER_IMAGE=
    if names=$(docker ps -a --format '{{.Names}}' 2>/dev/null); then
        :
    else
        rc=$?
        log "WARN image probe: docker ps failed exit=$rc"
        return 1
    fi
    if ! grep -qx 'palabras' <<< "$names"; then
        CONTAINER_STATE=absent
        return 0
    fi
    if CONTAINER_IMAGE=$(docker inspect --format '{{.Image}}' palabras 2>/dev/null) && [[ "$CONTAINER_IMAGE" == sha256:* ]]; then
        CONTAINER_STATE=image
        return 0
    fi
    CONTAINER_IMAGE=
    log 'WARN image probe: docker inspect did not return an image id for container palabras'
    return 1
}

# Sets RECORDED_IMAGE and RECORDED_IMAGE_SHA from $STATE_IMAGE; both stay empty when it is absent
# or unreadable, which is treated as "no record for this sha" rather than as a match.
read_state_image() {
    RECORDED_IMAGE= RECORDED_IMAGE_SHA=
    [[ -f "$STATE_IMAGE" ]] || return 0
    read -r RECORDED_IMAGE RECORDED_IMAGE_SHA < "$STATE_IMAGE" || true
}

# Tag the image the container is currently running before a rebuild re-points palabras:local and
# leaves it untagged (one `docker image prune` from loss). The tag name is derived from the image
# id, so repeated polls re-tag the same name instead of piling up: preservation is idempotent.
# A docker failure here aborts the deploy, exactly like a failed data backup: the rebuild must not
# replace a container whose rollback target could not be preserved.
preserve_running_image() {
    local image id12 tag resolved
    if ! container_image; then
        fail "preserve FAILED: cannot determine the running container (docker error listing containers); refusing to rebuild without a preserved rollback image"
    fi
    if [[ "$CONTAINER_STATE" == absent ]]; then
        log 'preserve skipped: container palabras absent; nothing running to preserve'
        return 0
    fi
    image=$CONTAINER_IMAGE
    id12=${image#sha256:}
    id12=${id12:0:12}
    tag="palabras:rollback-$id12"
    if ! docker image tag "$image" "$tag"; then
        fail "preserve FAILED: cannot tag running image $image as $tag; refusing to rebuild (the rebuild would leave the running image untagged and one docker image prune from loss)"
    fi
    if ! resolved=$(docker image inspect --format '{{.Id}}' "$tag" 2>/dev/null) || [[ "$resolved" != "$image" ]]; then
        fail "preserve FAILED: $tag does not resolve to running image $image; refusing to rebuild"
    fi
    log "preserve ok: running image $image tagged $tag"
    record_rollback_tag "$tag"
    prune_rollback_tags
}

record_rollback_tag() {
    local tag=$1 line tmp="$ROLLBACK_TAGS.next"
    : > "$tmp" || fail "preserve FAILED: cannot write $tmp"
    if [[ -f "$ROLLBACK_TAGS" ]]; then
        while IFS= read -r line || [[ -n "$line" ]]; do
            [[ -n "$line" && "$line" != "$tag" ]] || continue
            printf '%s\n' "$line" >> "$tmp" || fail "preserve FAILED: cannot write $tmp"
        done < "$ROLLBACK_TAGS"
    fi
    printf '%s\n' "$tag" >> "$tmp" || fail "preserve FAILED: cannot write $tmp"
    mv -f "$tmp" "$ROLLBACK_TAGS" || fail "preserve FAILED: cannot update $ROLLBACK_TAGS"
}

# Retention rule: keep the newest ROLLBACK_KEEP preserved images — the outgoing image of the
# current rebuild plus the previous one — and untag the rest. Only tag names this script created
# are ever touched; a tag that cannot be removed (still referenced) is kept and logged.
prune_rollback_tags() {
    local tag tmp="$ROLLBACK_TAGS.next" total seen=0
    local -a tags=()
    [[ -f "$ROLLBACK_TAGS" ]] || return 0
    while IFS= read -r tag || [[ -n "$tag" ]]; do
        [[ -n "$tag" ]] || continue
        tags+=("$tag")
    done < "$ROLLBACK_TAGS"
    total=${#tags[@]}
    (( total > ROLLBACK_KEEP )) || return 0
    : > "$tmp" || { log "WARN preserve prune: cannot write $tmp; keeping $ROLLBACK_TAGS unchanged"; return 0; }
    for tag in "${tags[@]}"; do
        seen=$((seen + 1))
        if (( seen > total - ROLLBACK_KEEP )); then
            printf '%s\n' "$tag" >> "$tmp"
            continue
        fi
        case "$tag" in
            palabras:rollback-*) ;;
            *) log "WARN preserve prune: not a rollback tag, keeping $tag"; printf '%s\n' "$tag" >> "$tmp"; continue ;;
        esac
        if docker image rm "$tag"; then
            log "preserve prune: untagged $tag (retention: newest $ROLLBACK_KEEP rollback tags kept)"
        else
            log "WARN preserve prune: could not untag $tag; keeping it in $ROLLBACK_TAGS"
            printf '%s\n' "$tag" >> "$tmp"
        fi
    done
    mv -f "$tmp" "$ROLLBACK_TAGS" || log "WARN preserve prune: cannot update $ROLLBACK_TAGS"
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
read_state_image
running=
if container_image; then
    running=$CONTAINER_IMAGE
fi
tag_image=$(docker image inspect --format '{{.Id}}' palabras:local 2>/dev/null || true)
log "images: running=${running:-none} recorded=${RECORDED_IMAGE:-none} recorded-sha=${RECORDED_IMAGE_SHA:-none} tag=${tag_image:-none} container=$CONTAINER_STATE"
# The running container must be the image this sha deployed; health alone proves nothing about
# which build is live. With a record for the target sha, only that record decides. Without one
# (older state file, or a deployed.sha written by hand after an acceptance) the tag the poll
# builds is the only available identity and is accepted, but logged as unverified.
image_verdict=none
if [[ -n "$running" ]]; then
    if [[ "$RECORDED_IMAGE" == sha256:* && "$RECORDED_IMAGE_SHA" == "$target" ]]; then
        if [[ "$running" == "$RECORDED_IMAGE" ]]; then
            image_verdict=recorded
        fi
    elif [[ -n "$tag_image" && "$running" == "$tag_image" ]]; then
        image_verdict=tag
    fi
fi
if [[ "$previous" == "$target" && "$health" == healthy && "$image_verdict" != none ]]; then
    cmp -s deploy/unraid/docker-compose.yml "$DEPLOY/docker-compose.yml" || fail 'compose mirror drift; restore versioned template or remove deployed.sha to rebuild; never edit mirror'
    # A healthy container with no accounts is exactly how data loss went unnoticed: /healthz
    # never touched the database. Refuse the green poll instead of reporting success.
    require_accounts 'no-change poll'
    if [[ "$image_verdict" == tag ]]; then
        log "no change sha=$target; healthy; running image $running matches palabras:local but no recorded image id for this sha; no rebuild (unverified against $STATE_IMAGE)"
    else
        log "no change sha=$target; healthy; running image $running matches recorded image; no rebuild"
    fi
    exit 0
fi
reason=sha-changed
if [[ "$previous" == "$target" ]]; then
    if [[ "$health" != healthy ]]; then
        reason=container-unhealthy
    else
        reason=image-mismatch
    fi
fi
log "deploying sha=$target reason=$reason (previous=$previous health=$health running=${running:-none} recorded=${RECORDED_IMAGE:-none} recorded-sha=${RECORDED_IMAGE_SHA:-none} container=$CONTAINER_STATE)"
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
preserve_running_image
backup_data
# Build identity: compose interpolates COMMIT_SHA into the build args, and it must be the sha
# this build actually uses. HEAD is read here, after the fast-forward and immediately before the
# build, so a checkout that moved (or a recorded deployed.sha that names another commit) cannot
# stamp the wrong sha into the image. `git -C "$REPO"` names the same checkout the compose
# context builds, so there is no second repo path to keep in sync. A failure to read it is not
# fatal: the build proceeds and the image reports "unknown", which is visible in /healthz.
if commit_sha=$(git -C "$REPO" rev-parse HEAD 2>/dev/null) && [[ -n "$commit_sha" ]]; then
    export COMMIT_SHA="$commit_sha"
else
    export COMMIT_SHA=unknown
    log "WARN cannot read the commit sha being built from $REPO; building with COMMIT_SHA=unknown"
fi
log "building sha=$COMMIT_SHA (target=$target previous=$previous)"
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
        deployed_image=$(docker inspect --format '{{.Image}}' palabras 2>/dev/null || true)
        if [[ "$deployed_image" != sha256:* ]]; then
            # Never write a wrong id: an unknown one makes the next poll compare against
            # palabras:local instead (logged as unverified), which cannot loop.
            deployed_image=unknown
            log "WARN cannot read the deployed container's image id; recording it as unknown"
        fi
        printf '%s %s\n' "$deployed_image" "$target" > "$STATE_IMAGE.next"
        mv -f "$STATE_IMAGE.next" "$STATE_IMAGE"
        printf '%s\n' "$target" > "$STATE.next"
        mv -f "$STATE.next" "$STATE"
        log "DEPLOY OK sha=$target healthy image=$deployed_image (previous=$previous)"
        exit 0
    fi
    log "health retry=$attempt/36 status=$health"
    sleep 5
done
docker logs --tail 30 palabras
fail "sha=$target did not become healthy after 36 attempts; inspect container; pause and rollback manually"
