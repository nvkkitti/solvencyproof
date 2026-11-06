use axum::{http::StatusCode, routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::net::SocketAddr;

//Bemeneti adatmodell (Ezt küldi a C# Entity Framework alapján)
#[derive(Deserialize)]
pub struct SolvencyRequest {
    pub institution_id: String,
    pub total_assets: u64,
    pub total_liabilities: u64,
    // Az ügyfélszámlákból képzett Merkle-levelek (SHA-256, "0x" prefixált hex)
    #[serde(default)]
    pub leaves: Vec<String>,
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

// Merkle-fa felépítése alulról felfelé; a C# MerkleTreeBuilder-rel azonos algoritmus:
// szülő = SHA256(bal || jobb), páratlan elemszámnál az utolsó elem önmagával párosul.
fn merkle_root(leaves: &[[u8; 32]]) -> [u8; 32] {
    let mut layer: Vec<[u8; 32]> = leaves.to_vec();
    while layer.len() > 1 {
        layer = layer
            .chunks(2)
            .map(|pair| {
                let left = pair[0];
                let right = if pair.len() == 2 { pair[1] } else { pair[0] };
                let mut hasher = Sha256::new();
                hasher.update(left);
                hasher.update(right);
                hasher.finalize().into()
            })
            .collect();
    }
    layer[0]
}

fn parse_leaf(hex_leaf: &str) -> Result<[u8; 32], String> {
    let bytes = hex::decode(hex_leaf.trim_start_matches("0x"))
        .map_err(|e| format!("Érvénytelen hex levél: {e}"))?;
    bytes
        .try_into()
        .map_err(|_| "A Merkle-levélnek pontosan 32 bájtosnak kell lennie".to_string())
}

//A STARK bizonyíték generálása és EVM-kompatibilisre formázása
async fn generate_solvency_proof(
    Json(payload): Json<SolvencyRequest>,
) -> Result<Json<EvmProofResponse>, (StatusCode, String)> {
    println!(
        "[1/3] Kérés érkezett: intézmény = {}, levelek száma = {}",
        payload.institution_id,
        payload.leaves.len()
    );

    if payload.leaves.is_empty() {
        return Err((StatusCode::BAD_REQUEST, "Legalább egy Merkle-levél szükséges".into()));
    }
    if payload.total_assets < payload.total_liabilities {
        return Err((StatusCode::UNPROCESSABLE_ENTITY, "A fedezet kisebb a kötelezettségnél, nem generálható bizonyíték".into()));
    }

    let leaves = payload
        .leaves
        .iter()
        .map(|leaf| parse_leaf(leaf))
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| (StatusCode::BAD_REQUEST, e))?;

    let root = merkle_root(&leaves);
    println!("[2/3] Merkle-gyökér: 0x{}", hex::encode(root));

    // Szimuláljuk, hogy a prover visszaad egy 32 bájtos tömbökből álló listát (Field Elements).
    // Az elemek az adatokból származnak, így a bizonyíték minden audit esetén más.
    // A matematikai könyvtárak jellemzően Little-Endian bájttömbökkel dolgoznak!
    let mut commitment_hasher = Sha256::new();
    commitment_hasher.update(payload.total_assets.to_le_bytes());
    commitment_hasher.update(payload.total_liabilities.to_le_bytes());
    commitment_hasher.update(root);
    let commitment: [u8; 32] = commitment_hasher.finalize().into();

    let mock_stark_proof_bytes: Vec<[u8; 32]> = vec![commitment, Sha256::digest(commitment).into()];

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

    //Nyilvános bemenetek (szintén "0x" prefixált hex)
    // Solidity: uint256[] publicInputs
    let public_inputs = vec![
        // [0] Fedezet/kötelezettség commitment
        format!("0x{}", hex::encode(commitment)),
        // [1] Kötelezettségek Merkle-gyökere (T)
        format!("0x{}", hex::encode(root)),
    ];

    println!("[3/3] Bizonyíték elkészült, válasz visszaküldve ({} elem)", proof_elements.len());

    // Válasz visszaküldése a C#-nak
    Ok(Json(EvmProofResponse {
        proof_elements,
        public_inputs,
    }))
}
