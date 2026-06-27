# Running TomYum on k3s

k3s is a lightweight, conformant Kubernetes distribution — the manifests in
[`../k8s/`](../k8s/) apply unchanged. This file only records k3s-specific deltas.

## Notes / overrides

- **Ingress**: k3s ships Traefik by default. We standardise on **Caddy** (see
  `../caddy/Caddyfile`) for edge + asset fronting. Either disable Traefik
  (`--disable traefik` when installing k3s) and run Caddy as a Deployment +
  LoadBalancer Service, or keep Traefik for cluster ingress and put Caddy only in
  front of object storage. Decide per deployment; document the choice in an ADR if it
  becomes load-bearing.
- **Storage**: k3s `local-path` provisioner is fine for dev MySQL/Redis PVCs. For
  anything real, use managed MySQL and a properly operated Redis — do not rely on
  `local-path` for primary persistence.
- **Load balancer**: k3s bundles ServiceLB (Klipper). It is adequate for a single-node
  or homelab deployment; use a real LB (MetalLB / cloud LB) for multi-node.
- **Resource limits**: set requests/limits on the server Deployment before scaling
  replicas; the scaffold manifest omits them for readability.

## Quick start (single node, dev)

```bash
# Build images from the repo root.
docker build -f deploy/docker/Dockerfile --build-arg PROJECT=src/TomYum.Server/TomYum.Server.csproj -t tomyum/server:dev .

# Import into k3s containerd (no registry needed for local dev).
k3s ctr images import <(docker save tomyum/server:dev)

# Apply.
kubectl apply -f deploy/k8s/tomyum.yaml
```

See `memories/architecture/decisions/0003-cross-node-coordination.md` for why the
server scales horizontally and what still needs a Redis backplane.
