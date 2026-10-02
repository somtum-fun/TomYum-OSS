# Reference pp sidecar (akatsuki-pp-rs). Multi-stage: build with the Rust toolchain, ship a slim runtime.
FROM rust:1-slim-bookworm AS build
WORKDIR /app
# git is needed to fetch the akatsuki-pp-rs git dependency.
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
COPY Cargo.toml Cargo.lock ./
COPY src ./src
# The akatsuki-pp-rs build this sidecar runs (its /version), read from Cargo.lock.
RUN export AKATSUKI_PP_COMMIT="$(grep -A2 '^name = "akatsuki-pp"' Cargo.lock | sed -n 's/.*#\([0-9a-f]*\)"$/\1/p')" \
           AKATSUKI_PP_VERSION="$(grep -A1 '^name = "akatsuki-pp"' Cargo.lock | sed -n 's/^version = "\(.*\)"$/\1/p')" \
    && echo "akatsuki-pp $AKATSUKI_PP_VERSION ($AKATSUKI_PP_COMMIT)" \
    && cargo build --release --locked

FROM debian:bookworm-slim
# ca-certificates: TLS roots for downloading .osu over HTTPS (reqwest uses rustls).
# curl: used by the container healthcheck to probe /health.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/target/release/tomyum-pp-sidecar /usr/local/bin/tomyum-pp-sidecar
ENV PORT=9000
EXPOSE 9000
ENTRYPOINT ["tomyum-pp-sidecar"]
