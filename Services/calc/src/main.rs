use axum::{routing::post, Router};
use tokio::net::TcpListener;

mod handlers; // A handlers.rs fájl betöltése modulként
use handlers::generate_proof; // A függvény importálása

#[tokio::main]
async fn main() {
    let app = Router::new()
        .route("/prove", post(generate_proof));

    let listener = TcpListener::bind("0.0.0.0:3000").await.unwrap();
    println!("Axum szerver fut a 3000-es porton...");
    
    axum::serve(listener, app).await.unwrap();
}