#!/bin/bash
# Runs in a throwaway directory; git is real, Docker/curl are fakes.
# Tests process boundaries and durable SHA state without server credentials.
set -Eeuo pipefail
# Relocate every fixture/deployment path before executing the test body. Never
# write host deployment paths, even when this test runs inside a host container.
if [[ ${PALABRAS_SMOKE_SANDBOXED:-0} != 1 ]]; then
    source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
    fixture=$(mktemp -d /tmp/opencode/palabras-smoke.XXXXXX)
    trap 'rm -rf "$fixture"' EXIT
    body=$(< "${BASH_SOURCE[0]}")
    body=${body//\/mnt\/user\/repos/$fixture/repos}
    body=${body//\/mnt\/user\/docker/$fixture/docker}
    body=${body//\/var\/lock/$fixture/lock}
    body=${body//\/fixture/$fixture}
    body=${body//\/src/$source_root}
    script=$(< "$source_root/deploy/unraid-deploy.sh")
    script=${script//\/mnt\/user\/repos/$fixture/repos}
    script=${script//\/mnt\/user\/docker/$fixture/docker}
    script=${script//\/var\/lock/$fixture/lock}
    printf '%s\n' "$script" > "$fixture/unraid-deploy.sh"
    body=${body//\/bin\/bash $source_root\/deploy\/unraid-deploy.sh/\/bin\/bash $fixture\/unraid-deploy.sh}
    printf '%s\n' "$body" > "$fixture/smoke.sh"
    PALABRAS_SMOKE_SANDBOXED=1 /bin/bash "$fixture/smoke.sh"
    exit
fi
mkdir -p /fixture/bin /mnt/user/repos /mnt/user/docker/palabras /var/lock /fixture/appdata/keys
# Data-directory fixture for the guards: a non-empty SQLite-shaped database, cookie keys, and
# a symlink inside keys/ that must never be dereferenced by the backup copy.
printf 'SQLite format 3\000fixture database, not a real database\n' > /fixture/appdata/palabras.db
printf 'fixture cookie key\n' > /fixture/appdata/keys/key-fixture.xml
printf 'outside the data directory\n' > /fixture/outside-secret.xml
ln -s /fixture/outside-secret.xml /fixture/appdata/keys/outside-link.xml
export PATH="/fixture/bin:$PATH"
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
git init --bare /fixture/origin.git
git init -b main /fixture/author
git -C /fixture/author config user.name Test
git -C /fixture/author config user.email test@example.invalid
mkdir -p /fixture/author/deploy/unraid
cp /src/deploy/unraid/docker-compose.yml /fixture/author/deploy/unraid/
printf 'first\n' > /fixture/author/version
git -C /fixture/author add .
git -C /fixture/author commit -m first
git -C /fixture/author push /fixture/origin.git main
git clone --branch main /fixture/origin.git /mnt/user/repos/palabras
printf 'APPDATA_PATH=/fixture/appdata\n' > /mnt/user/docker/palabras/.env
cat > /fixture/bin/git <<'GIT'
#!/bin/bash
if [[ "$*" == 'remote get-url origin' ]]; then
    if [[ ${TEST_ORIGIN_MODE:-ok} == remote-unreadable ]]; then
        printf 'fixture remote URL unreadable\n' >&2
        exit 128
    fi
    printf '%s\n' "${TEST_ORIGIN_URL:-git@github-personal:michaelsieben/palabras.git}"
elif [[ "$*" == 'config --get remote.origin.url' ]]; then
    case "${TEST_ORIGIN_MODE:-ok}" in
        absent) exit 1;;
        empty) exit 0;;
        unreadable) printf 'fixture config unreadable\n' >&2; exit 128;;
        *) printf '%s\n' "${TEST_ORIGIN_URL:-git@github-personal:michaelsieben/palabras.git}";;
    esac
elif [[ "$*" == 'rev-parse --abbrev-ref HEAD' && ${TEST_BRANCH_FAILURE:-0} == 1 ]]; then
    printf 'fixture branch detection failed\n' >&2
    exit 128
elif [[ "$1" == diff && ${TEST_DIFF_FAILURE:-0} == 1 ]]; then
    printf 'fixture diff failed\n' >&2
    exit 128
elif [[ "$1" == fetch ]]; then
    if [[ ${TEST_FETCH_FAILURE:-0} == 1 ]]; then
        printf 'fixture fetch authentication failed\n' >&2
        exit 128
    fi
    exec /usr/bin/git fetch --prune /fixture/origin.git refs/heads/main:refs/remotes/origin/main
else
    exec /usr/bin/git "$@"
fi
GIT
cat > /fixture/bin/docker <<'DOCKER'
#!/bin/bash
set -euo pipefail
case "$1" in
    inspect) if [[ -e /fixture/unhealthy ]]; then printf 'starting\n'; else printf 'healthy\n'; fi;;
    compose) if [[ "$*" == *' up -d --build' ]]; then printf 'build\n' >> /fixture/builds; : > /fixture/rebuilt; fi;;
    logs) printf 'fake container logs\n';;
    exec)
        # Account fixture for the post-deploy check. TEST_ACCOUNT_ROWS is the count before a
        # rebuild in this poll, TEST_ACCOUNT_ROWS_POST the count after it.
        case "${TEST_CLI_MODE:-ok}" in
            unavailable) printf 'Error response from daemon: container palabras is not running\n' >&2; exit 1;;
            no-header) printf 'Usage: user create <owner|reviewer> --username <name>\n'; exit 0;;
        esac
        if [[ -e /fixture/rebuilt ]]; then
            rows=${TEST_ACCOUNT_ROWS_POST:-${TEST_ACCOUNT_ROWS:-2}}
        else
            rows=${TEST_ACCOUNT_ROWS:-2}
        fi
        printf 'ROLE\tUSERNAME\tCREATED (UTC)\tLAST LOGIN (UTC)\n'
        for ((i = 0; i < rows; i++)); do
            printf 'owner\tuser%s\t2026-10-18T00:00:00.0000000Z\tnever\n' "$i"
        done
        ;;
    *) exit 1;;
esac
DOCKER
cat > /fixture/bin/curl <<'CURL'
#!/bin/bash
printf '{"status":"ok","version":"test"}'
CURL
cat > /fixture/bin/sleep <<'SLEEP'
#!/bin/bash
# No wall-clock wait for deterministic timeout coverage.
exit 0
SLEEP
chmod +x /fixture/bin/*
run() { /bin/bash /src/deploy/unraid-deploy.sh; }
count() { wc -l < /fixture/builds; }
backup_entries() { ls -1 /mnt/user/docker/palabras/backups 2>/dev/null || true; }
expect_failure() { if run; then printf 'FAIL expected nonzero\n'; exit 1; fi; }
expect_message() {
    local message=$1 output rc
    if output=$(run 2>&1); then
        printf 'FAIL expected nonzero: %s\n' "$message"
        exit 1
    else
        rc=$?
    fi
    printf '%s\n' "$output"
    if [[ "$output" != *"$message"* ]]; then
        printf 'FAIL missing expected message: %s\n' "$message"
        return 1
    fi
    if [[ "$output" == *'checkout is not main'* ]]; then
        printf 'FAIL stale mislabel present: checkout is not main\n'
        return 1
    fi
    printf 'PASS expected failure exit=%s: %s\n' "$rc" "$message"
}

mv /mnt/user/repos/palabras/.git /fixture/checkout.git
expect_message 'repository checkout missing'
mkdir /mnt/user/repos/palabras/.git
expect_message 'git rev-parse failed exit=128'
rmdir /mnt/user/repos/palabras/.git
mv /fixture/checkout.git /mnt/user/repos/palabras/.git
export TEST_BRANCH_FAILURE=1
expect_message 'git rev-parse failed exit=128'
unset TEST_BRANCH_FAILURE
export TEST_DIFF_FAILURE=1
expect_message 'git diff failed exit=128'
unset TEST_DIFF_FAILURE
export TEST_ORIGIN_MODE=absent
expect_message 'origin URL absent'
export TEST_ORIGIN_MODE=empty
expect_message 'origin URL absent'
export TEST_ORIGIN_MODE=unreadable
expect_message 'git config failed reading origin URL exit=128'
export TEST_ORIGIN_MODE=remote-unreadable
expect_message 'git remote failed reading origin URL exit=128'
unset TEST_ORIGIN_MODE
export TEST_ORIGIN_URL=https://github.com/michaelsieben/palabras.git
expect_message 'unexpected origin URL'
unset TEST_ORIGIN_URL
export TEST_FETCH_FAILURE=1
expect_message 'git fetch failed exit=128'
unset TEST_FETCH_FAILURE
git -C /mnt/user/repos/palabras checkout -b unexpected
expect_message 'checkout branch is unexpected, expected main'
git -C /mnt/user/repos/palabras checkout main

if [[ $(id -u) == 0 ]]; then
    chown -R 99:100 /mnt/user/repos/palabras
    if output=$(/usr/bin/git -C /mnt/user/repos/palabras rev-parse HEAD 2>&1); then
        printf 'FAIL expected dubious ownership\n'
        exit 1
    fi
    [[ "$output" == *'detected dubious ownership'* ]]
    printf '%s\n' "$output"
    /usr/bin/git -c safe.directory=/mnt/user/repos/palabras -C /mnt/user/repos/palabras rev-parse HEAD
    run
    chown -R 0:0 /mnt/user/repos/palabras
    printf 'PASS root poll deploys checkout owned by uid 99:100 without global gitconfig\n'
else
    printf 'SKIP foreign ownership fixture requires root\n'
    run
fi

[[ $(count) == 1 ]]
sha=$(git -C /mnt/user/repos/palabras rev-parse HEAD)
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS first healthy deploy records sha\n'
run
[[ $(count) == 1 ]]
printf 'PASS no change skips rebuild\n'

# --- step 08 guard 2: pre-rebuild backup outside the data directory ---
backup_count=$(backup_entries | wc -l)
[[ "$backup_count" == 1 ]] || { printf 'FAIL expected one pre-rebuild backup, found %s\n' "$backup_count"; exit 1; }
backup_dir=/mnt/user/docker/palabras/backups/$(backup_entries | tail -n 1)
[[ -s "$backup_dir/palabras.db" ]] || { printf 'FAIL backup database copy missing or empty\n'; exit 1; }
[[ -f "$backup_dir/keys/key-fixture.xml" ]]
[[ -L "$backup_dir/keys/outside-link.xml" ]]
[[ "$(readlink "$backup_dir/keys/outside-link.xml")" == "/fixture/outside-secret.xml" ]]
printf 'PASS backup copies database and keys without following symlinks\n'

# --- step 08 guard 1: database missing or empty aborts before anything is touched ---
mv /fixture/appdata/palabras.db /fixture/appdata/palabras.db.away
expect_message 'data guard FAILED (poll): /fixture/appdata/palabras.db is missing'
[[ $(count) == 1 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
[[ $(backup_entries | wc -l) == 1 ]]
cmp -s /src/deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
printf 'PASS missing database aborts the poll with the mover cause and records nothing\n'
: > /fixture/appdata/palabras.db
expect_message 'data guard FAILED (poll): /fixture/appdata/palabras.db is 0 bytes (empty)'
[[ $(count) == 1 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
rm /fixture/appdata/palabras.db
ln -s /fixture/outside-secret.xml /fixture/appdata/palabras.db
expect_message 'data guard FAILED (poll): /fixture/appdata/palabras.db is a symlink'
[[ $(count) == 1 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
rm /fixture/appdata/palabras.db
mv /fixture/appdata/palabras.db.away /fixture/appdata/palabras.db
run
[[ $(count) == 1 ]]
printf 'PASS empty and symlinked databases abort; a real database resumes normally\n'

# --- step 08 guard 3: account check on the healthy no-change poll ---
export TEST_ACCOUNT_ROWS=0 TEST_ACCOUNT_ROWS_POST=0
expect_message 'account check FAILED (no-change poll): ZERO accounts in container palabras'
[[ $(count) == 1 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS zero accounts fails a healthy no-change poll without touching deployed.sha\n'
export TEST_CLI_MODE=unavailable
expect_message 'cannot invoke the accounts CLI in container palabras'
export TEST_CLI_MODE=no-header
expect_message 'accounts CLI output unrecognised'
unset TEST_CLI_MODE
unset TEST_ACCOUNT_ROWS TEST_ACCOUNT_ROWS_POST
[[ $(count) == 1 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS uninvocable and unrecognised accounts CLI fail as distinct, labelled errors\n'

printf '# unexpected local edit\n' >> /mnt/user/docker/palabras/docker-compose.yml
expect_failure
[[ $(count) == 1 ]]
cp /src/deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
printf 'PASS compose mirror drift rejected\n'

# --- step 08 guard 2: retention keeps the newest 5 and deletes nothing else ---
for old in 20200101T000000Z-0000001 20200102T000000Z-0000002 20200103T000000Z-0000003 \
    20200104T000000Z-0000004 20200105T000000Z-0000005 20200106T000000Z-0000006; do
    mkdir -p "/mnt/user/docker/palabras/backups/$old"
    printf 'old backup\n' > "/mnt/user/docker/palabras/backups/$old/palabras.db"
done
printf 'keep me\n' > /mnt/user/docker/palabras/backups/keepme.txt

printf 'second\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m second
git -C /fixture/author push /fixture/origin.git main
run
[[ $(count) == 2 ]]
sha=$(git -C /fixture/author rev-parse HEAD)
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS new main commit fast-forwards and deploys\n'
[[ $(backup_entries | grep -cE '^[0-9]{8}T[0-9]{6}Z-') == 5 ]] || { printf 'FAIL expected 5 retained backups\n'; exit 1; }
[[ ! -e /mnt/user/docker/palabras/backups/20200101T000000Z-0000001 ]]
[[ ! -e /mnt/user/docker/palabras/backups/20200102T000000Z-0000002 ]]
[[ ! -e /mnt/user/docker/palabras/backups/20200103T000000Z-0000003 ]]
[[ -d /mnt/user/docker/palabras/backups/20200104T000000Z-0000004 ]]
[[ -f /mnt/user/docker/palabras/backups/keepme.txt ]]
printf 'PASS backup retention keeps newest 5 and removes nothing else\n'

touch /mnt/user/docker/palabras/paused
run
[[ $(count) == 2 ]]
rm /mnt/user/docker/palabras/paused
printf 'PASS paused skips rebuild\n'
printf 'dirty\n' >> /mnt/user/repos/palabras/version
expect_failure
[[ $(count) == 2 ]]
git -C /mnt/user/repos/palabras restore version
printf 'PASS tracked edits rejected\n'
git -C /mnt/user/repos/palabras checkout --detach HEAD
expect_message 'checkout has detached HEAD'
git -C /mnt/user/repos/palabras checkout main
printf 'PASS rollback detached head rejected\n'

printf 'third\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m third
git -C /fixture/author push /fixture/origin.git main
touch /fixture/unhealthy
expect_failure
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
[[ $(count) == 3 ]]
printf 'PASS health timeout nonzero; last healthy sha retained\n'
rm /fixture/unhealthy
run
[[ $(count) == 4 ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$(git -C /fixture/author rev-parse HEAD)" ]]
printf 'PASS failed deployment retried without resetting git\n'
flock /var/lock/palabras-deploy.lock /bin/bash /src/deploy/unraid-deploy.sh
[[ $(count) == 4 ]]
printf 'PASS concurrent deploy lock skips rebuild\n'
truncate -s 1048576 /mnt/user/docker/palabras/deploy.log
run
[[ -f /mnt/user/docker/palabras/deploy.log.1 ]]
[[ $(count) == 4 ]]
printf 'PASS log rotation preserves no-change behavior\n'

printf 'local-only\n' >> /mnt/user/repos/palabras/version
git -C /mnt/user/repos/palabras add version
git -C /mnt/user/repos/palabras -c user.name=Test -c user.email=test@example.invalid commit -m local-only
local_sha=$(git -C /mnt/user/repos/palabras rev-parse HEAD)
printf 'remote-only\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m remote-only
git -C /fixture/author push /fixture/origin.git main
expect_failure
[[ $(git -C /mnt/user/repos/palabras rev-parse HEAD) == "$local_sha" ]]
[[ $(count) == 4 ]]
printf 'PASS diverged main rejected without reset or rebuild\n'

# --- step 08 guard 3: post-deploy account check on the rebuild path ---
git -C /mnt/user/repos/palabras reset --hard origin/main
deployed=$(< /mnt/user/docker/palabras/deployed.sha)
builds=$(count)
export TEST_ACCOUNT_ROWS=2 TEST_ACCOUNT_ROWS_POST=0
rm -f /fixture/rebuilt
expect_message 'account check FAILED (post-deploy sha='
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$deployed" ]]
[[ $(count) == $((builds + 1)) ]]
printf 'PASS zero accounts after a rebuild fails the deploy and keeps the previous sha\n'
export TEST_ACCOUNT_ROWS_POST=2
rm -f /fixture/rebuilt
run
[[ $(count) == $((builds + 2)) ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$(git -C /mnt/user/repos/palabras rev-parse HEAD)" ]]
printf 'PASS accounts present after a rebuild records the deploy as good\n'

printf 'fourth\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m fourth
git -C /fixture/author push /fixture/origin.git main
deployed=$(< /mnt/user/docker/palabras/deployed.sha)
export TEST_ACCOUNT_ROWS=2 TEST_ACCOUNT_ROWS_POST=1
rm -f /fixture/rebuilt
expect_message 'accounts dropped from 2 to 1'
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$deployed" ]]
printf 'PASS a partial account loss fails the deploy and keeps the previous sha\n'
export TEST_ACCOUNT_ROWS_POST=2
rm -f /fixture/rebuilt
run
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$(git -C /fixture/author rev-parse HEAD)" ]]
unset TEST_ACCOUNT_ROWS TEST_ACCOUNT_ROWS_POST
printf 'PASS restored accounts deploy normally after both failures\n'

# --- step 08 guard 1 on the rebuild path: abort before mirror, backup or rebuild ---
printf 'fifth\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m fifth
git -C /fixture/author push /fixture/origin.git main
deployed=$(< /mnt/user/docker/palabras/deployed.sha)
builds=$(count)
backups=$(backup_entries | wc -l)
mv /fixture/appdata/palabras.db /fixture/appdata/palabras.db.away
expect_message 'data guard FAILED (poll): /fixture/appdata/palabras.db is missing'
[[ $(count) == "$builds" ]]
[[ $(backup_entries | wc -l) == "$backups" ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$deployed" ]]
cmp -s /src/deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
[[ "$(git -C /mnt/user/repos/palabras rev-parse HEAD)" == "$deployed" ]]
mv /fixture/appdata/palabras.db.away /fixture/appdata/palabras.db
run
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$(git -C /mnt/user/repos/palabras rev-parse HEAD)" ]]
printf 'PASS a due deploy aborts on a missing database before mirror, backup or rebuild\n'

# --- step 08 guard 2: a broken backup target aborts the deploy, it is never ignored ---
printf 'sixth\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m sixth
git -C /fixture/author push /fixture/origin.git main
deployed=$(< /mnt/user/docker/palabras/deployed.sha)
builds=$(count)
rm -rf /mnt/user/docker/palabras/backups
printf 'not a directory\n' > /mnt/user/docker/palabras/backups
expect_message 'backup FAILED: cannot create /mnt/user/docker/palabras/backups'
[[ $(count) == "$builds" ]]
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$deployed" ]]
cmp -s /src/deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
rm /mnt/user/docker/palabras/backups
run
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$(git -C /mnt/user/repos/palabras rev-parse HEAD)" ]]
printf 'PASS backup failure aborts before mirror and rebuild; restored target deploys\n'

# --- step 08: a first deploy (no deployed.sha yet) must still be able to provision ---
rm -f /mnt/user/docker/palabras/deployed.sha
mv /fixture/appdata/palabras.db /fixture/appdata/palabras.db.bootstrap
if ! output=$(run 2>&1); then
    printf 'FAIL first deploy without a database should still provision\n%s\n' "$output"
    exit 1
fi
if [[ "$output" != *'backup skipped: '* ]]; then
    printf 'FAIL expected the backup to be skipped on a first deploy\n%s\n' "$output"
    exit 1
fi
[[ -f /mnt/user/docker/palabras/deployed.sha ]]
mv /fixture/appdata/palabras.db.bootstrap /fixture/appdata/palabras.db
printf 'PASS first deploy without deployed.sha provisions and skips the backup\n'
