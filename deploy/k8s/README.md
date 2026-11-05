# Deploy Mail Router to the centimo cluster

Mail Router runs in the **same cluster as centimo**, as a standalone tool like `k8s/status` and `k8s/sonarqube` there:

| | |
|---|---|
| Namespace | `centimo-mailrouter` |
| Image | `ghcr.io/auxitech/mailrouter:<version>` (pull secret `regcred`) |
| URL | https://mailrouter.auxitech.ch (ingress `nginx`, cert-manager `letsencrypt-prod`, TLS secret `auxitech-tls-mailrouter`) |
| Secrets | `sealed-secret-mailrouter-env.yaml`, a SealedSecret sealed with centimo's `sealed-secrets-cert.pem`. Safe to commit |
| Storage | 1Gi PVC on the default storage class (SQLite, Microsoft sign-in token, cookie keys) |

> **Run exactly ONE instance across Docker AND Kubernetes.** Two instances polling the same mailbox send duplicate replies and forwards. Stop the local Docker container before the cluster pod goes live (step 5). The Deployment is pinned to 1 replica with the `Recreate` strategy, so never scale it up.

## 1. DNS

Point `mailrouter.auxitech.ch` at the cluster ingress (179.237.75.38, same as `lb.centimo.ch`). cert-manager issues the certificate automatically through the HTTP-01 challenge once DNS resolves.

## 2. Build and push the image

```bash
docker login ghcr.io -u YOUR_GITHUB_USER          # PAT with write:packages
docker build -t ghcr.io/auxitech/mailrouter:1.0.0 .
docker push ghcr.io/auxitech/mailrouter:1.0.0
```

## 3. Pull secret in the new namespace (once)

`regcred` is per namespace, so create it like in centimo's README:

```bash
kubectl create namespace centimo-mailrouter
kubectl -n centimo-mailrouter create secret docker-registry regcred \
  --docker-server=ghcr.io --docker-username=YOUR_GITHUB_USER --docker-password=YOUR_GHCR_PAT
```

## 4. Deploy

With your kubectl context on the centimo cluster:

```bash
kubectl apply -k deploy/k8s
kubectl -n centimo-mailrouter rollout status deploy/mailrouter
kubectl -n centimo-mailrouter get pods,pvc,ingress,certificate
```

The Sealed Secrets controller (already in the cluster, `kube-system`) decrypts `mailrouter-env` automatically.

## 5. Bring your existing data from Docker and switch over

This moves rules, settings, history and the Microsoft sign-in. Do it right after step 4, **before** clicking Connect in the new instance.

```bash
docker compose stop                                   # local instance stops processing NOW
docker run --rm -v ai-assistant-0365_mailrouter-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/mailrouter-data.tgz -C /data .

POD=$(kubectl -n centimo-mailrouter get pod -l app=mailrouter -o jsonpath='{.items[0].metadata.name}')
kubectl -n centimo-mailrouter cp mailrouter-data.tgz $POD:/tmp/mailrouter-data.tgz
kubectl -n centimo-mailrouter exec $POD -- sh -c 'rm -rf /data/* && tar xzf /tmp/mailrouter-data.tgz -C /data && rm /tmp/mailrouter-data.tgz'
kubectl -n centimo-mailrouter rollout restart deploy/mailrouter

rm mailrouter-data.tgz                                # contains the sign-in token
docker compose down                                   # keep the volume (no -v) as a backup
```

Open https://mailrouter.auxitech.ch, log in with the same admin password, and check the dashboard shows **Connected as …** and that the last-run time is moving.

## Changing a secret

Edit `.env`, re-seal, and apply:

```bash
tr -d '\r' < .env > /tmp/env.clean
kubectl create secret generic mailrouter-env -n centimo-mailrouter --from-env-file=/tmp/env.clean --dry-run=client -o yaml \
  | kubeseal --cert ../centimo/sealed-secrets-cert.pem --format yaml > deploy/k8s/sealed-secret-mailrouter-env.yaml
rm /tmp/env.clean
kubectl apply -k deploy/k8s && kubectl -n centimo-mailrouter rollout restart deploy/mailrouter
```

## Upgrades

```bash
docker build -t ghcr.io/auxitech/mailrouter:1.0.1 . && docker push ghcr.io/auxitech/mailrouter:1.0.1
# set newTag: "1.0.1" in kustomization.yaml
kubectl apply -k deploy/k8s
```

## Backup

```bash
POD=$(kubectl -n centimo-mailrouter get pod -l app=mailrouter -o jsonpath='{.items[0].metadata.name}')
kubectl -n centimo-mailrouter exec $POD -- tar czf - -C /data . > mailrouter-backup-$(date +%F).tgz
```

The archive contains the Microsoft sign-in token, so keep it private.

## Troubleshooting

| Symptom | Check |
|---|---|
| `ImagePullBackOff` | `regcred` exists in `centimo-mailrouter`; image/tag pushed to GHCR |
| `CreateContainerConfigError` | Secret not unsealed: `kubectl -n centimo-mailrouter get sealedsecret,secret`, controller logs in `kube-system` |
| Certificate not ready | DNS for `mailrouter.auxitech.ch`; `kubectl -n centimo-mailrouter describe certificate auxitech-tls-mailrouter` |
| PVC `Pending` | `kubectl get storageclass` (a default class must exist) |
| Login loops back to the login page | Ingress must send `X-Forwarded-Proto` (ingress-nginx does by default) |

Logs are collected by the cluster's Alloy/Loki stack like every other pod, so they're visible in Grafana (grafana.centimo.ch) under namespace `centimo-mailrouter`.
