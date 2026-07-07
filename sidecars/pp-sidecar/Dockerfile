# Reference pp sidecar (akatsuki-pp-rs). Multi-stage: build with the Rust toolchain, ship a slim runtime.
FROM rust:1-slim AS build
WORKDIR /app
# git is needed to fetch the akatsuki-pp-rs git dependency.
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
COPY Cargo.toml Cargo.lock ./
COPY src ./src
RUN cargo build --release

FROM debian:bookworm-slim
# ca-certificates: TLS roots for downloading .osu over HTTPS (reqwest uses rustls).
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/target/release/tomyum-pp-sidecar /usr/local/bin/tomyum-pp-sidecar
ENV PORT=9000
EXPOSE 9000
ENTRYPOINT ["tomyum-pp-sidecar"]
