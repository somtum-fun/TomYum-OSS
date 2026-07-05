# Configuration

TomYum uses a layered configuration system (see ADR-0008):

```
appsettings.json  →  appsettings.Development.json  →  tomyum.yaml  →  TOMYUM_* env vars
(lowest priority)                                                       (highest priority)
```

## Quick setup

1. **Copy the example file:**
   ```bash
   cp config/tomyum.yaml.example config/tomyum.yaml
   ```

2. **Edit `tomyum.yaml`** for your deployment (domain, redis host, storage providers, etc.)

3. **Set secrets via environment variables** (never commit these):
   ```bash
   export TOMYUM_DATABASE__PASSWORD=yourpassword
   export TOMYUM_STORAGE__PROVIDERS__R2__ACCESSKEY=...
   export TOMYUM_STORAGE__PROVIDERS__R2__SECRETKEY=...
   ```

## What goes where

**`tomyum.yaml`** (gitignored) — deployment-specific settings:
- Domain, bind address, ports
- Database/Redis hosts
- Storage provider configuration (buckets, endpoints, regions)
- Feature flags, plugin config

**Environment variables** (prefixed `TOMYUM_`) — secrets only:
- Database password
- S3/R2/Azure access keys
- OAuth signing keys
- Webhook URLs with tokens

**`appsettings.json` / `appsettings.Development.json`** (committed) — framework defaults and dev-mode overrides. Rarely edited.

## For local development

If running MySQL/Redis via docker-compose, the default `tomyum.yaml.example` values should work after you copy it to `tomyum.yaml`. Only the database password needs to be set:

```bash
cp config/tomyum.yaml.example config/tomyum.yaml
export TOMYUM_DATABASE__PASSWORD=tomyum
dotnet run --project src/TomYum.Server
```

See `deploy/docker/docker-compose.yml` for spinning up local MySQL + Redis containers.
