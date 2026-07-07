//! TomYum reference pp sidecar.
//!
//! Implements the TomYum `HttpPerformanceCalculator` contract (see ADR-0010): `POST /pp` with a
//! score, returns `{ "pp": <f64>, "starRating": <f64> }`. pp is computed with **akatsuki-pp-rs**,
//! a fork of rosu-pp with relax/autopilot-tuned algorithms, so relax/autopilot scores get proper
//! pp instead of the vanilla formula.
//!
//! The `mode` field is the vanilla ruleset (0=osu,1=taiko,2=catch,3=mania); Relax/Autopilot are
//! read from the `mods` legacy bitmask, which akatsuki-pp uses to select its rx/ap path.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;

use akatsuki_pp::{Beatmap, Difficulty, Performance};
use akatsuki_pp::model::mode::GameMode;
use axum::http::StatusCode;
use axum::{extract::State, routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PpRequest {
    beatmap_id: i32,
    mode: u8, // vanilla ruleset 0-3
    mods: u32,
    n300: u32,
    n100: u32,
    n50: u32,
    #[serde(rename = "nGeki")]
    n_geki: u32,
    #[serde(rename = "nKatu")]
    n_katu: u32,
    #[serde(rename = "nMiss")]
    n_miss: u32,
    max_combo: u32,
    #[allow(dead_code)]
    passed: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PpResponse {
    pp: f64,
    star_rating: f64,
}

#[derive(Clone)]
struct AppState {
    http: reqwest::Client,
    // Cache raw .osu bytes by beatmap id to avoid re-downloading on every request.
    cache: Arc<Mutex<HashMap<i32, Arc<Vec<u8>>>>>,
}

fn game_mode(vanilla: u8) -> GameMode {
    match vanilla {
        1 => GameMode::Taiko,
        2 => GameMode::Catch,
        3 => GameMode::Mania,
        _ => GameMode::Osu,
    }
}

async fn fetch_osu(state: &AppState, beatmap_id: i32) -> Result<Arc<Vec<u8>>, String> {
    if let Some(bytes) = state.cache.lock().await.get(&beatmap_id).cloned() {
        return Ok(bytes);
    }
    let url = format!("https://osu.ppy.sh/osu/{beatmap_id}");
    let resp = state.http.get(&url).send().await.map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("osu download {} returned {}", beatmap_id, resp.status()));
    }
    let bytes = Arc::new(resp.bytes().await.map_err(|e| e.to_string())?.to_vec());
    state.cache.lock().await.insert(beatmap_id, bytes.clone());
    Ok(bytes)
}

async fn calc(
    State(state): State<AppState>,
    Json(req): Json<PpRequest>,
) -> Result<Json<PpResponse>, (StatusCode, String)> {
    match compute(&state, &req).await {
        Ok(resp) => Ok(Json(resp)),
        // Return 5xx (not 200 + pp:0) so the TomYum client can fall back to another backend
        // instead of silently recording 0 pp for a transient sidecar-side failure.
        Err(e) => {
            tracing::warn!("pp calc failed for beatmap {}: {}", req.beatmap_id, e);
            Err((StatusCode::INTERNAL_SERVER_ERROR, e))
        }
    }
}

async fn compute(state: &AppState, req: &PpRequest) -> Result<PpResponse, String> {
    let bytes = fetch_osu(state, req.beatmap_id).await?;

    let mut map = Beatmap::from_bytes(&bytes).map_err(|e| e.to_string())?;
    let target = game_mode(req.mode);
    // Convert the beatmap to the requested ruleset if it differs (e.g. an osu! map played as catch).
    if map.mode != target {
        map.convert_mut(target, &req.mods.into())
            .map_err(|e| format!("convert to {:?} failed: {e:?}", target))?;
    }

    let diff = Difficulty::new().mods(req.mods).calculate(&map);
    let stars = diff.stars();

    let perf = Performance::new(diff)
        .mods(req.mods)
        .combo(req.max_combo)
        .n300(req.n300)
        .n100(req.n100)
        .n50(req.n50)
        .n_geki(req.n_geki)
        .n_katu(req.n_katu)
        .misses(req.n_miss)
        .calculate();

    Ok(PpResponse { pp: perf.pp(), star_rating: stars })
}

async fn health() -> &'static str {
    "ok"
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt::init();

    let state = AppState {
        http: reqwest::Client::builder()
            .user_agent("tomyum-pp-sidecar/0.1")
            .build()
            .expect("http client"),
        cache: Arc::new(Mutex::new(HashMap::new())),
    };

    let app = Router::new()
        .route("/pp", post(calc))
        .route("/health", axum::routing::get(health))
        .with_state(state);

    let port: u16 = std::env::var("PORT").ok().and_then(|p| p.parse().ok()).unwrap_or(9000);
    let addr = SocketAddr::from(([0, 0, 0, 0], port));
    tracing::info!("tomyum-pp-sidecar listening on {addr}");

    let listener = tokio::net::TcpListener::bind(addr).await.expect("bind");
    axum::serve(listener, app).await.expect("serve");
}
