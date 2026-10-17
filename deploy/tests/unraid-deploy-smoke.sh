#!/bin/bash
# Run only in a disposable Linux SDK container; git is real, Docker/curl are fakes.
# Tests process boundaries and durable SHA state without server credentials.
set -Eeuo pipefail
[[ -f /.dockerenv ]] || { printf 'Refusing host execution; use disposable Docker container\n'; exit 1; }
mkdir -p /fixture/bin /mnt/user/repos /mnt/user/docker/palabras /var/lock
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
printf 'APPDATA_PATH=/data\n' > /mnt/user/docker/palabras/.env
cat > /fixture/bin/git <<'GIT'
#!/bin/bash
if [[ "$*" == 'remote get-url origin' ]]; then
    printf 'git@github-personal:michaelsieben/palabras.git\n'
elif [[ "$1" == fetch ]]; then
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
    compose) if [[ "$*" == *' up -d --build' ]]; then printf 'build\n' >> /fixture/builds; fi;;
    logs) printf 'fake container logs\n';;
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
expect_failure() { if run; then printf 'FAIL expected nonzero\n'; exit 1; fi; }

run
[[ $(count) == 1 ]]
sha=$(git -C /mnt/user/repos/palabras rev-parse HEAD)
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS first healthy deploy records sha\n'
run
[[ $(count) == 1 ]]
printf 'PASS no change skips rebuild\n'
printf '# unexpected local edit\n' >> /mnt/user/docker/palabras/docker-compose.yml
expect_failure
[[ $(count) == 1 ]]
cp /src/deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
printf 'PASS compose mirror drift rejected\n'

printf 'second\n' >> /fixture/author/version
git -C /fixture/author add version
git -C /fixture/author commit -m second
git -C /fixture/author push /fixture/origin.git main
run
[[ $(count) == 2 ]]
sha=$(git -C /fixture/author rev-parse HEAD)
[[ $(< /mnt/user/docker/palabras/deployed.sha) == "$sha" ]]
printf 'PASS new main commit fast-forwards and deploys\n'

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
expect_failure
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
