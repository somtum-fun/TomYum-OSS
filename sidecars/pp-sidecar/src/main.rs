//! TomYum reference pp sidecar.
//!
//! Implements the TomYum `HttpPerformanceCalculator` contract (see ADR-0010): `POST /pp` with a
//! score, returns `{ "pp": <f64>, "starRating": <f64> }`. pp is computed with **akatsuki-pp-rs**,
//! a fork of rosu-pp with relax/autopilot-tuned algorithms, so relax/autopilot scores get proper
//! pp instead of the vanilla formula.
//!
//! The `mode` field is the vanilla ruleset (0=osu,1=taiko,2=catch,3=mania); Relax/Autopilot are
//! read from the `mods` legacy bitmask. As akatsuki-pp-py (what Akatsuki itself runs) does it, osu!
//! Relax goes to Akatsuki's own relax algorithm (`osu_2019`), and everything else (Autopilot
//! included) to the main calculator. `lazer` says whether the play was made on lazer (stable when
//! missing). `GET /version` names the akatsuki-pp-rs build, for the admin panel.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use akatsuki_pp::osu_2019::OsuPP;
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
    #[serde(default)]
    lazer: bool,
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
    // L1: raw .osu bytes by beatmap id, in-memory (process lifetime).
    cache: Arc<Mutex<HashMap<i32, Arc<Vec<u8>>>>>,
    // L2: on-disk cache dir (survives restarts; shareable with the C# official backend).
    cache_dir: PathBuf,
}

// Unique suffix for temp files so concurrent writers of the same id can't corrupt each other.
static TMP_SEQ: AtomicU64 = AtomicU64::new(0);

fn game_mode(vanilla: u8) -> GameMode {
    match vanilla {
        1 => GameMode::Taiko,
        2 => GameMode::Catch,
        3 => GameMode::Mania,
        _ => GameMode::Osu,
    }
}

async fn fetch_osu(state: &AppState, beatmap_id: i32) -> Result<Arc<Vec<u8>>, String> {
    // L1: in-memory.
    if let Some(bytes) = state.cache.lock().await.get(&beatmap_id).cloned() {
        return Ok(bytes);
    }
    // L2: on-disk. A locally-dropped custom map's .osu is served here without hitting osu.ppy.sh.
    let path = state.cache_dir.join(format!("{beatmap_id}.osu"));
    if let Ok(disk) = tokio::fs::read(&path).await {
        if !disk.is_empty() {
            let bytes = Arc::new(disk);
            state.cache.lock().await.insert(beatmap_id, bytes.clone());
            return Ok(bytes);
        }
    }
    // Miss: download, then populate L2 (best-effort) and L1.
    let url = format!("https://osu.ppy.sh/osu/{beatmap_id}");
    let resp = state.http.get(&url).send().await.map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("osu download {} returned {}", beatmap_id, resp.status()));
    }
    let bytes = Arc::new(resp.bytes().await.map_err(|e| e.to_string())?.to_vec());
    write_cache_file(&state.cache_dir, beatmap_id, &bytes).await;
    state.cache.lock().await.insert(beatmap_id, bytes.clone());
    Ok(bytes)
}

/// Persist a downloaded .osu to the L2 cache. Best-effort: a cache-write failure must never fail a
/// pp calc. Writes to a unique temp file then atomically renames, so a reader never sees a partial.
async fn write_cache_file(dir: &Path, beatmap_id: i32, bytes: &[u8]) {
    let seq = TMP_SEQ.fetch_add(1, Ordering::Relaxed);
    let tmp = dir.join(format!("{beatmap_id}.{seq}.tmp"));
    let final_path = dir.join(format!("{beatmap_id}.osu"));
    if tokio::fs::write(&tmp, bytes).await.is_ok() {
        if tokio::fs::rename(&tmp, &final_path).await.is_err() {
            let _ = tokio::fs::remove_file(&tmp).await;
        }
    }
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

    // osu! Relax: Akatsuki's relax algorithm, with its own star rating.
    const RELAX: u32 = 1 << 7;
    if target == GameMode::Osu && req.mods & RELAX != 0 {
        // Unlike the main calculator, osu_2019 takes the hit counts as given: keep them within the
        // map's objects (taking from the 300s first), and a failed play counts only what it reached.
        let objects = map.hit_objects.len() as u32;
        let (n100, n50) = (req.n100.min(objects), req.n50.min(objects));
        let n_miss = req.n_miss.min(objects);
        let n300 = req.n300.min(objects.saturating_sub(n100 + n50 + n_miss));
        let mut calc = OsuPP::from_map(&map)
            .mods(req.mods)
            .combo(req.max_combo)
            .n300(n300)
            .n100(n100)
            .n50(n50)
            .misses(n_miss);
        if !req.passed {
            calc = calc.passed_objects((n300 + n100 + n50 + n_miss).min(objects));
        }
        let perf = calc.calculate();
        return Ok(PpResponse { pp: perf.pp, star_rating: perf.difficulty.stars });
    }

    let diff = Difficulty::new().mods(req.mods).lazer(req.lazer).calculate(&map);
    let stars = diff.stars();

    let perf = Performance::new(diff)
        .lazer(req.lazer)
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

/// The akatsuki-pp-rs build: its crate version and git commit (set at build time from Cargo.lock).
async fn version() -> Json<serde_json::Value> {
    Json(serde_json::json!({
        "library": "akatsuki-pp-rs",
        "version": option_env!("AKATSUKI_PP_VERSION").unwrap_or("unknown"),
        "commit": option_env!("AKATSUKI_PP_COMMIT").unwrap_or("unknown"),
    }))
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt::init();

    let cache_dir: PathBuf = std::env::var("OSU_CACHE_DIR")
        .unwrap_or_else(|_| "data/osu-cache".to_string())
        .into();
    if let Err(e) = std::fs::create_dir_all(&cache_dir) {
        tracing::warn!("could not create osu cache dir {}: {}", cache_dir.display(), e);
    }
    tracing::info!(".osu disk cache at {}", cache_dir.display());

    let state = AppState {
        http: reqwest::Client::builder()
            .user_agent("tomyum-pp-sidecar/0.1")
            .build()
            .expect("http client"),
        cache: Arc::new(Mutex::new(HashMap::new())),
        cache_dir,
    };

    let app = Router::new()
        .route("/pp", post(calc))
        .route("/health", axum::routing::get(health))
        .route("/version", axum::routing::get(version))
        .with_state(state);

    let port: u16 = std::env::var("PORT").ok().and_then(|p| p.parse().ok()).unwrap_or(9000);
    let addr = SocketAddr::from(([0, 0, 0, 0], port));
    tracing::info!("tomyum-pp-sidecar listening on {addr}");

    let listener = tokio::net::TcpListener::bind(addr).await.expect("bind");
    axum::serve(listener, app).await.expect("serve");
}
