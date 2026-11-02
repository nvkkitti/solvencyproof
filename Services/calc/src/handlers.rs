use axum::{Json, response::IntoResponse};
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
pub struct ProofRequest {
    pub public_input: String,
}

#[derive(Serialize)]
pub struct ProofResponse {
    pub proof_data: String,
    pub status: String,
}

pub async fn generate_proof(Json(payload): Json<ProofRequest>) -> impl IntoResponse {
    // Itt hívja meg a zk-STARK bizonyíték generátort
    let response = ProofResponse {
        proof_data: format!("Bizonyíték a(z) {} bemenethez generálva", payload.public_input),
        status: "success".to_string(),
    };

    Json(response)
}