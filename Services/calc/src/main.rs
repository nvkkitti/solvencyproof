use axum::{routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use std::net::SocketAddr;

//Bemeneti adatmodell (Ezt küldi a C# Entity Framework alapján)
#[derive(Deserialize)]
pub struct SolvencyRequest {
    pub institution_id: String,
    pub total_assets: u64,
    pub total_liabilities: u64,
    //ide jönnek a Merkle-fa levelek és felhasználói egyenlegek
}

//Kimeneti adatmodell (EVM kompatibilis formátum)
#[derive(Serialize)]
pub struct EvmProofResponse {
    // uint256[] formátumnak megfelelő hexadecimális sztring tömb
    pub proof_elements: Vec<String>,
    pub public_inputs: Vec<String>,
}

#[tokio::main]
async fn main() {
    let app = Router::new()
        .route("/api/v1/generate-proof", post(generate_solvency_proof));

    let addr = SocketAddr::from(([127, 0, 0, 1], 3000));
    println!("Axum STARK prover API fut a {} címen", addr);
    
    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    axum::serve(listener, app).await.unwrap();
}

//A STARK bizonyíték generálása és EVM-kompatibilisre formázása
async fn generate_solvency_proof(
    Json(payload): Json<SolvencyRequest>,
) -> Json<EvmProofResponse> {
    println!("Bizonyíték generálása: {}", payload.institution_id);

    // Szimuláljuk, hogy a prover visszaad egy 32 bájtos tömbökből álló listát (Field Elements)
    // A matematikai könyvtárak jellemzően Little-Endian bájttömbökkel dolgoznak!
    let mock_stark_proof_bytes: Vec<[u8; 32]> = vec![
        [255u8; 32], // 1. mezőelem
        [128u8; 32], // 2. mezőelem
    ];

    //Konvertálás EVM uint256[] kompatibilis Hex sztringekké
    let proof_elements: Vec<String> = mock_stark_proof_bytes
        .into_iter()
        .map(|mut element| {
            // HA a prover Little-Endian kimenetet ad (pl. x86 architektúrán gyakori),
            // itt meg kell fordítani a bájtsorrendet, mert az EVM Big-Endian!
            element.reverse(); 
            
            // Konvertálás "0x" kezdetű hex sztringgé
            format!("0x{}", hex::encode(element))
        })
        .collect();

    //Nyilvános bemenetek (pl. Merkle gyökér) beállítása (szintén "0x" prefixált hex)
    // Solidity: uint256[] publicInputs
    let public_inputs = vec![
        // [0] Pl. Assets Commitment hash (v)
        "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef".to_string(),
        // [1] Pl. Liabilities Merkle Root (T)
        "0xabcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890".to_string(),
    ];

    // Válasz visszaküldése a C#-nak
    Json(EvmProofResponse {
        proof_elements,
        public_inputs,
    })
}