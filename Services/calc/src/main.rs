//! The zero-knowledge solvency prover service.
//!
//! The C# application sends the private witness (account leaves, assets, blinding factor) over
//! loopback; this service builds a real zk-STARK (see `air.rs`, `stark.rs`) and returns the proof
//! together with the public inputs. Only the public inputs and the proof ever leave the institution.

mod air;
mod hash;
mod stark;
mod trace;

use axum::extract::DefaultBodyLimit;
use axum::{Json, Router, http::StatusCode, routing::post};
use p3_field::PrimeCharacteristicRing;
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use std::net::SocketAddr;

use crate::hash::{
    Digest, F, LeafData, TreeError, depth_for, digest_from_bytes, digest_to_bytes,
    field_from_canonical,
};
use crate::stark::{ProveError, prove_solvency, verify_solvency};
use crate::trace::{PublicInputs, TraceError, Witness, public_inputs};

/// One account of the liabilities tree. Field elements are canonical `u64` values (`< 2^64 - 2^32 + 1`).
#[derive(Deserialize)]
pub struct LeafRequest {
    /// The 128-bit account id as four 32-bit limbs.
    pub id: [u64; 4],
    pub salt: [u64; 4],
    /// Balance in the smallest currency unit, below `2^40`.
    pub balance: u64,
}

#[derive(Deserialize)]
pub struct SolvencyRequest {
    /// The audit identifier; the proof is bound to it through a nonce.
    pub institution_id: String,
    /// Assets in the smallest currency unit.
    pub total_assets: u64,
    /// The 256-bit blinding factor of the assets commitment, as four canonical field elements.
    pub assets_blinding: [u64; 4],
    /// The commitment and the Merkle root the caller computed itself ("0x" + 32 bytes). The prover
    /// recomputes both and refuses to prove if they differ, so the two implementations stay in sync.
    pub expected_assets_commitment: String,
    pub expected_root: String,
    pub leaves: Vec<LeafRequest>,
}

#[derive(Serialize)]
pub struct EvmProofResponse {
    /// `uint256[]` for the contract: the SHA-256 digest of the serialized STARK proof. The full
    /// proof is too large for calldata and is verified by [`verify_proof`].
    pub proof_elements: Vec<String>,
    /// `[0]` assets commitment, `[1]` liabilities Merkle root, `[2]` audit nonce, all as `0x` hex.
    pub public_inputs: Vec<String>,
    /// The full serialized proof, hex encoded; input of [`verify_proof`].
    pub proof: String,
    /// Depth of the liabilities tree the proof was made for.
    pub depth: usize,
    /// The prover verified its own proof before answering.
    pub verified: bool,
}

#[derive(Deserialize)]
pub struct VerifyRequest {
    pub depth: usize,
    pub proof: String,
    pub assets_commitment: String,
    pub root: String,
    /// The audit identifier the proof was made for; the nonce is derived from it.
    pub institution_id: String,
}

#[derive(Serialize)]
pub struct VerifyResponse {
    pub valid: bool,
    pub error: Option<String>,
}

type ApiError = (StatusCode, String);

#[tokio::main]
async fn main() {
    let app = Router::new()
        .route("/api/v1/generate-proof", post(generate_solvency_proof))
        .route("/api/v1/verify-proof", post(verify_proof))
        // A witness with many accounts is large; the default 2 MB limit is too small.
        .layer(DefaultBodyLimit::max(256 * 1024 * 1024));

    // The address can be overridden (e.g. for a second instance next to a running one), the default is loopback:3000.
    let addr: SocketAddr = std::env::var("PROVER_ADDR")
        .unwrap_or_else(|_| "127.0.0.1:3000".to_string())
        .parse()
        .expect("A PROVER_ADDR érvénytelen cím (pl. 127.0.0.1:3000)");
    println!("Axum zk-STARK prover API fut a {} címen", addr);

    let listener = match tokio::net::TcpListener::bind(addr).await {
        Ok(listener) => listener,
        Err(e) => {
            eprintln!(
                "Nem sikerült a {addr} címre kötni: {e}. Valószínűleg már fut egy prover példány ezen a porton."
            );
            std::process::exit(1);
        }
    };
    axum::serve(listener, app).await.unwrap();
}

fn bad_request(message: impl ToString) -> ApiError {
    (StatusCode::BAD_REQUEST, message.to_string())
}

fn field(value: u64) -> Result<F, ApiError> {
    field_from_canonical(value).map_err(|e| bad_request(e))
}

fn field4(values: [u64; 4]) -> Result<[F; 4], ApiError> {
    Ok([field(values[0])?, field(values[1])?, field(values[2])?, field(values[3])?])
}

fn parse_hex32(text: &str, name: &str) -> Result<[u8; 32], ApiError> {
    let bytes = hex::decode(text.trim_start_matches("0x"))
        .map_err(|e| bad_request(format!("{name}: érvénytelen hex: {e}")))?;
    bytes
        .try_into()
        .map_err(|_| bad_request(format!("{name}: pontosan 32 bájtosnak kell lennie")))
}

fn parse_digest(text: &str, name: &str) -> Result<Digest, ApiError> {
    digest_from_bytes(&parse_hex32(text, name)?)
        .map_err(|e: TreeError| bad_request(format!("{name}: {e}")))
}

fn hex32(bytes: &[u8; 32]) -> String {
    format!("0x{}", hex::encode(bytes))
}

/// The nonce is derived from the audit id, so a proof cannot be replayed for another audit.
fn nonce_for(institution_id: &str) -> F {
    let hash = Sha256::digest(institution_id.as_bytes());
    F::from_u64(u64::from_be_bytes(hash[..8].try_into().expect("8 bytes")))
}

fn nonce_bytes(nonce: F) -> [u8; 32] {
    use p3_field::PrimeField64;
    let mut bytes = [0u8; 32];
    bytes[24..].copy_from_slice(&nonce.as_canonical_u64().to_be_bytes());
    bytes
}

/// The caller gets a generic message; the detail stays in the server log.
fn internal_error(detail: impl std::fmt::Display) -> ApiError {
    eprintln!("Belső hiba: {detail}");
    (StatusCode::INTERNAL_SERVER_ERROR, "A bizonyítás belső hibába ütközött.".to_string())
}

fn trace_error(error: TraceError) -> ApiError {
    match error {
        TraceError::Insolvent { .. } => (StatusCode::UNPROCESSABLE_ENTITY, error.to_string()),
        other => bad_request(other),
    }
}

/// Generates the zk-STARK proof and checks it before it is returned.
async fn generate_solvency_proof(
    Json(payload): Json<SolvencyRequest>,
) -> Result<Json<EvmProofResponse>, ApiError> {
    println!(
        "[1/3] Kérés érkezett: intézmény = {}, levelek száma = {}",
        payload.institution_id,
        payload.leaves.len()
    );

    if payload.leaves.is_empty() {
        return Err(bad_request("Legalább egy levél szükséges"));
    }

    let leaves = payload
        .leaves
        .iter()
        .map(|leaf| {
            Ok(LeafData {
                id: field4(leaf.id)?,
                salt: field4(leaf.salt)?,
                balance: leaf.balance,
            })
        })
        .collect::<Result<Vec<_>, ApiError>>()?;
    let witness = Witness {
        leaves,
        assets: payload.total_assets,
        blinding: field4(payload.assets_blinding)?,
    };
    let expected_commitment = parse_digest(&payload.expected_assets_commitment, "expected_assets_commitment")?;
    let expected_root = parse_digest(&payload.expected_root, "expected_root")?;

    let depth = depth_for(witness.leaves.len());
    let nonce = nonce_for(&payload.institution_id);

    // The public inputs are cheap; compare them with the caller's before the expensive proof.
    let (expected, _) = public_inputs(depth, &witness, nonce).map_err(trace_error)?;
    if expected.root != expected_root {
        return Err((
            StatusCode::CONFLICT,
            "A Rust és a hívó Merkle-gyökere eltér: a két Poseidon2 megvalósítás nem egyezik".into(),
        ));
    }
    if expected.commitment != expected_commitment {
        return Err((
            StatusCode::CONFLICT,
            "A Rust és a hívó eszköz-elkötelezettsége eltér: a két Poseidon2 megvalósítás nem egyezik".into(),
        ));
    }
    println!("[2/3] Merkle-gyökér és elkötelezettség egyezik, mélység = {depth}, bizonyítás készül...");

    let (proof_bytes, public) = tokio::task::spawn_blocking(move || {
        let (proof_bytes, public) = prove_solvency(depth, &witness, nonce)?;
        // Never hand out a proof that does not verify.
        verify_solvency(depth, &proof_bytes, &public)
            .map_err(|e| ProveError::Prover(e.to_string()))?;
        Ok::<_, ProveError>((proof_bytes, public))
    })
    .await
    .map_err(internal_error)?
    .map_err(|error| match error {
        ProveError::Trace(trace) => trace_error(trace),
        other => internal_error(other),
    })?;

    println!(
        "[3/3] Bizonyíték elkészült és ellenőrizve ({} bájt), válasz visszaküldve",
        proof_bytes.len()
    );

    Ok(Json(EvmProofResponse {
        proof_elements: vec![hex32(&Sha256::digest(&proof_bytes).into())],
        public_inputs: vec![
            hex32(&digest_to_bytes(&public.commitment)),
            hex32(&digest_to_bytes(&public.root)),
            hex32(&nonce_bytes(public.nonce)),
        ],
        proof: format!("0x{}", hex::encode(&proof_bytes)),
        depth,
        verified: true,
    }))
}

/// Verifies a proof against public inputs only. No witness is involved, anyone can call this.
async fn verify_proof(Json(request): Json<VerifyRequest>) -> Result<Json<VerifyResponse>, ApiError> {
    let public = PublicInputs {
        root: parse_digest(&request.root, "root")?,
        commitment: parse_digest(&request.assets_commitment, "assets_commitment")?,
        nonce: nonce_for(&request.institution_id),
    };
    let proof = hex::decode(request.proof.trim_start_matches("0x"))
        .map_err(|e| bad_request(format!("proof: érvénytelen hex: {e}")))?;
    if !(hash::MIN_DEPTH..=hash::MAX_DEPTH).contains(&request.depth) {
        return Err(bad_request("depth: kívül esik a megengedett tartományon"));
    }

    let depth = request.depth;
    let result = tokio::task::spawn_blocking(move || verify_solvency(depth, &proof, &public))
        .await
        .map_err(internal_error)?;

    Ok(Json(match result {
        Ok(()) => VerifyResponse { valid: true, error: None },
        Err(error) => {
            eprintln!("Az ellenőrzés sikertelen: {error}");
            VerifyResponse { valid: false, error: Some("A bizonyíték nem ellenőrizhető.".to_string()) }
        }
    }))
}
