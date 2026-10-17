# T-20261017-1516 / Step 04 — Proxy bind

Answer to the user's port question and the fix that unblocks configuring the proxy now.

## PROXY (read-only inspect, 2026-10-17)

```
$ ssh unraid-docker 'docker ps --format "{{.Names}}\t{{.Image}}" | grep -i -E "nginx|proxy"'
nginx-proxy-manager	jc21/nginx-proxy-manager:latest

$ ssh unraid-docker "docker inspect nginx-proxy-manager --format '{{.HostConfig.NetworkMode}}'"
br0

$ ssh unraid-docker "docker inspect nginx-proxy-manager --format '{{json .NetworkSettings.Networks}}'"
br0   -> IPAMConfig 192.168.68.2, Gateway 192.168.68.1, IPPrefixLen 22
sieben -> Gateway 172.18.0.1, IP .37, /16 (secondary bridge)

$ ssh unraid-docker "docker inspect nginx-proxy-manager --format '{{.HostConfig.PortBindings}}'"
map[]          # no published host ports — consistent with macvlan br0
```

Conclusion: the proxy is a **macvlan (br0) container at 192.168.68.2**. A
`127.0.0.1:18480` publish on the Unraid host is unreachable for it, so the M0 loopback
binding would have blocked the proxy setup. The proxy reaches the host LAN IP
`192.168.68.66` out through the bridge, which is the expected macvlan path.

No mutating remote command was run. Nothing was started, stopped, edited or restarted.

## Change

`docker-compose.yml`

```yaml
    ports:
      # 18480: chosen because it is absent from the Unraid host-port scan (2026-10-17;
      # :8080 is already sabnzbd). Override with APP_PORT if it is ever taken.
      # 0.0.0.0, not 127.0.0.1: nginx-proxy-manager is a container on macvlan br0, so it
      # cannot reach the Unraid host's loopback. LAN-only; never forwarded on the WAN side.
      - "0.0.0.0:${APP_PORT:-18480}:8080"
```

`0.0.0.0` is reachable by a host-networked, bridge and macvlan proxy alike, so the user is
not blocked on the network-mode answer. `${APP_PORT:-18480}` stays overridable.

`README.md`, "Deploy on Unraid": exact nginx-proxy-manager Proxy Host settings, the
`http://palabras:8080` alternative (only if the app later joins the proxy's Docker
network), the 502-until-deployed statement, and the macvlan 502 check.

`AGENTS.md` line 12 and `.env.example` corrected to match (both stated the loopback bind).
`.env.example` gains `APP_PORT=18480`.

Nothing committed, staged or pushed: working tree carries three tracked-file modifications
plus `.env.example`, so a concurrent reviewer can see them. Deploy, NPM, DNS and Unraid host
were not touched.

## Proxy configuration the user should enter

nginx-proxy-manager → Hosts → Proxy Hosts → Add Proxy Host:

| Field | Value |
|---|---|
| Domain Names | `palabras.siebentower.nl` |
| Scheme | `http` |
| Forward Hostname / IP | `192.168.68.66` |
| Forward Port | `18480` |
| Block Common Exploits | on |
| Websockets Support | off (no WS/SignalR; push is FCM) |
| SSL | existing Let's Encrypt cert for the domain |
| Force SSL / HTTP2 / HSTS | on / on / on |

Expect **502 until the container is deployed**. Deploying to Unraid is a user-run step.
If it 502s after deployment, from inside the proxy container:

```bash
docker exec nginx-proxy-manager curl -H "Host: palabras.siebentower.nl" http://192.168.68.66:18480/healthz
```

## Verification

```
$ cd D:\Visual Studio Repo\Personal\Palabras
$ docker compose config   # exit 0
...
    ports:
      - mode: ingress
        host_ip: 0.0.0.0
        target: 8080
        published: "18480"
        protocol: tcp
$ docker compose config --quiet   # exit 0
```

Raw output: `04-impl.log`. No build/test run: only compose YAML and Markdown changed, and
the rendered config is the check that matters here.

## Risks / follow-ups

- 18480 is now exposed on the LAN (Unraid firewall / macvlan peers can reach it). It must
  never be port-forwarded on the WAN side; the proxy owns 443.
- The read-only scan cannot see host processes; recheck 18480 before deployment.
- M1 should confirm the source IP the app sees (`172.x` Docker gateway or the macvlan
  proxy) before `Forwarding:KnownNetwork` is finalized — the published-port path changes the
  peer IP compared with the loopback design in ΔA.4.
