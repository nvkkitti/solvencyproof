//! The zero-knowledge STARK configuration: Goldilocks, Poseidon2 Merkle commitments, hiding FRI.

use p3_challenger::DuplexChallenger;
use p3_commit::ExtensionMmcs;
use p3_dft::Radix2DitParallel;
use p3_field::Field;
use p3_field::extension::BinomialExtensionField;
use p3_fri::{FriParameters, HidingFriPcs};
use p3_goldilocks::{Goldilocks, Poseidon2Goldilocks, default_goldilocks_poseidon2_8};
use p3_matrix::dense::RowMajorMatrix;
use p3_merkle_tree::MerkleTreeHidingMmcs;
use p3_symmetric::{PaddingFreeSponge, TruncatedPermutation};
use p3_uni_stark::{StarkConfig, prove, verify};
use rand::rngs::StdRng;

use crate::air::SolvencyAir;
use crate::hash::F;
use crate::trace::{PublicInputs, TraceError, Witness, generate_trace};

type Val = Goldilocks;
type Challenge = BinomialExtensionField<Val, 2>;
type Perm = Poseidon2Goldilocks<8>;
type Hash = PaddingFreeSponge<Perm, 8, 4, 4>;
type Compress = TruncatedPermutation<Perm, 2, 4, 8>;
type ValMmcs = MerkleTreeHidingMmcs<
    <Val as Field>::Packing,
    <Val as Field>::Packing,
    Hash,
    Compress,
    StdRng,
    2,
    4,
    4,
>;
type ChallengeMmcs = ExtensionMmcs<Val, Challenge, ValMmcs>;
type Dft = Radix2DitParallel<Val>;
type Pcs = HidingFriPcs<Val, Dft, ValMmcs, ChallengeMmcs, StdRng>;
type Challenger = DuplexChallenger<Val, Perm, 8, 4>;
pub type Config = StarkConfig<Pcs, Challenge, Challenger>;

/// Independent random codewords that mask the committed polynomials; at least the extension degree.
const NUM_RANDOM_CODEWORDS: usize = 4;

/// Builds a fresh configuration. The hiding randomness is seeded from the operating system, so the
/// masks of two proofs are independent and cannot be reproduced by an observer.
fn config() -> Config {
    let perm = default_goldilocks_poseidon2_8();
    let hash = Hash::new(perm.clone());
    let compress = Compress::new(perm.clone());
    let val_mmcs = ValMmcs::new(hash, compress, 0, rand::make_rng::<StdRng>());
    let challenge_mmcs = ChallengeMmcs::new(val_mmcs.clone());
    let fri_params = FriParameters::new_benchmark_zk(challenge_mmcs);
    let pcs = Pcs::new(
        Dft::default(),
        val_mmcs,
        fri_params,
        NUM_RANDOM_CODEWORDS,
        rand::make_rng::<StdRng>(),
    );
    Config::new(pcs, Challenger::new(perm))
}

#[derive(Debug, thiserror::Error)]
pub enum ProveError {
    #[error(transparent)]
    Trace(#[from] TraceError),
    #[error("the STARK prover failed: {0}")]
    Prover(String),
    #[error("the proof could not be serialized: {0}")]
    Serialize(String),
}

#[derive(Debug, thiserror::Error)]
pub enum VerifyError {
    #[error("the proof bytes are malformed: {0}")]
    Malformed(String),
    #[error("the STARK proof is invalid: {0}")]
    Invalid(String),
}

/// Proves the solvency statement and returns the serialized proof with its public inputs.
pub fn prove_solvency(
    depth: usize,
    witness: &Witness,
    nonce: F,
) -> Result<(Vec<u8>, PublicInputs), ProveError> {
    let air = SolvencyAir::new(depth);
    let (trace, public) = generate_trace(&air, witness, nonce)?;
    let bytes = prove_trace(&air, trace, &public)?;
    Ok((bytes, public))
}

fn prove_trace(
    air: &SolvencyAir,
    trace: RowMajorMatrix<F>,
    public: &PublicInputs,
) -> Result<Vec<u8>, ProveError> {
    let config = config();
    let proof = prove(&config, air, trace, &public.to_values())
        .map_err(|e| ProveError::Prover(format!("{e:?}")))?;
    postcard::to_stdvec(&proof).map_err(|e| ProveError::Serialize(e.to_string()))
}

/// Verifies a serialized proof against the public inputs. This is the real check: no witness is needed.
pub fn verify_solvency(
    depth: usize,
    proof_bytes: &[u8],
    public: &PublicInputs,
) -> Result<(), VerifyError> {
    let air = SolvencyAir::new(depth);
    let proof = postcard::from_bytes(proof_bytes).map_err(|e| VerifyError::Malformed(e.to_string()))?;
    verify(&config(), &air, &proof, &public.to_values())
        .map_err(|e| VerifyError::Invalid(format!("{e:?}")))
}

#[cfg(test)]
mod tests {
    use p3_field::{PrimeCharacteristicRing, PrimeField64};

    use super::*;
    use crate::hash::{LeafData, MIN_DEPTH};

    fn leaf(i: u64, balance: u64) -> LeafData {
        LeafData {
            id: [F::from_u64(i), F::from_u64(2 * i), F::from_u64(3 * i), F::from_u64(4)],
            salt: [F::from_u64(9 + i), F::from_u64(8), F::from_u64(7), F::from_u64(6)],
            balance,
        }
    }

    fn witness(assets: u64) -> Witness {
        Witness {
            leaves: vec![leaf(1, 150_050), leaf(2, 25_000_000), leaf(3, 0)],
            assets,
            blinding: [F::from_u64(11), F::from_u64(22), F::from_u64(33), F::from_u64(44)],
        }
    }

    #[test]
    fn a_solvent_institution_gets_a_valid_proof() {
        let nonce = F::from_u64(42);
        let (bytes, public) = prove_solvency(MIN_DEPTH, &witness(25_150_050), nonce).unwrap();
        eprintln!("proof size: {} bytes", bytes.len());
        verify_solvency(MIN_DEPTH, &bytes, &public).unwrap();
    }

    #[test]
    fn the_proof_does_not_verify_for_other_public_inputs() {
        let (bytes, public) = prove_solvency(MIN_DEPTH, &witness(30_000_000), F::from_u64(1)).unwrap();

        let mut other_nonce = public;
        other_nonce.nonce = F::from_u64(2);
        assert!(verify_solvency(MIN_DEPTH, &bytes, &other_nonce).is_err());

        let mut other_root = public;
        other_root.root[0] += F::ONE;
        assert!(verify_solvency(MIN_DEPTH, &bytes, &other_root).is_err());

        let mut other_commitment = public;
        other_commitment.commitment[3] += F::ONE;
        assert!(verify_solvency(MIN_DEPTH, &bytes, &other_commitment).is_err());
    }

    #[test]
    fn an_insolvent_institution_cannot_build_a_proof() {
        let error = prove_solvency(MIN_DEPTH, &witness(25_150_049), F::from_u64(1)).unwrap_err();
        assert!(matches!(error, ProveError::Trace(TraceError::Insolvent { .. })));
    }

    #[test]
    fn assets_that_exactly_cover_the_liabilities_are_solvent() {
        let (bytes, public) = prove_solvency(MIN_DEPTH, &witness(25_150_050), F::from_u64(5)).unwrap();
        verify_solvency(MIN_DEPTH, &bytes, &public).unwrap();
    }

    #[test]
    fn two_proofs_of_the_same_statement_differ() {
        // The hiding PCS masks the committed polynomials with fresh randomness every time.
        let witness = witness(26_000_000);
        let (first, public) = prove_solvency(MIN_DEPTH, &witness, F::from_u64(7)).unwrap();
        let (second, _) = prove_solvency(MIN_DEPTH, &witness, F::from_u64(7)).unwrap();
        assert_ne!(first, second);
        verify_solvency(MIN_DEPTH, &first, &public).unwrap();
        verify_solvency(MIN_DEPTH, &second, &public).unwrap();
    }

    #[test]
    fn the_proof_does_not_verify_for_another_tree_depth() {
        let (bytes, public) = prove_solvency(MIN_DEPTH, &witness(26_000_000), F::from_u64(7)).unwrap();
        assert!(verify_solvency(MIN_DEPTH + 1, &bytes, &public).is_err());
    }

    #[test]
    fn a_truncated_or_corrupted_proof_is_rejected() {
        let (bytes, public) = prove_solvency(MIN_DEPTH, &witness(26_000_000), F::from_u64(7)).unwrap();
        assert!(verify_solvency(MIN_DEPTH, &bytes[..bytes.len() / 2], &public).is_err());

        let mut corrupted = bytes.clone();
        let middle = corrupted.len() / 2;
        corrupted[middle] ^= 0x01;
        assert!(verify_solvency(MIN_DEPTH, &corrupted, &public).is_err());
    }

    /// Tries to prove a witness an honest prover refuses to prove, by building the trace anyway.
    /// The statement is rejected if the prover fails, panics, or produces a proof that does not verify.
    fn assert_forgery_is_rejected(witness: &Witness) {
        let air = SolvencyAir::new(MIN_DEPTH);
        let (trace, public) =
            crate::trace::generate_forged_trace(&air, witness, F::from_u64(3)).unwrap();

        let attempt = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            prove_trace(&air, trace, &public)
        }));
        match attempt {
            Err(_) | Ok(Err(_)) => {}
            Ok(Ok(bytes)) => assert!(
                verify_solvency(MIN_DEPTH, &bytes, &public).is_err(),
                "the verifier accepted a forged proof"
            ),
        }
    }

    #[test]
    fn insolvency_cannot_be_forged() {
        // One unit short: `assets - liabilities` is -1, which has no 62-bit decomposition.
        assert_forgery_is_rejected(&witness(25_150_049));
    }

    #[test]
    fn a_wrapped_around_balance_cannot_hide_liabilities() {
        // A balance of `p - 100` is -100 in the field and would shrink the total, but it is not a 40-bit number.
        let negative = F::ORDER_U64 - 100;
        let witness = Witness {
            leaves: vec![leaf(1, 1_000), leaf(2, negative)],
            assets: 900,
            blinding: [F::from_u64(1); 4],
        };
        assert_forgery_is_rejected(&witness);
    }

    fn splitmix64(state: &mut u64) -> u64 {
        *state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = *state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn sz3_field4(state: &mut u64) -> [F; 4] {
        std::array::from_fn(|_| F::from_u64(splitmix64(state) >> 32))
    }

    /// One fresh witness of `SZ3_ACCOUNTS` accounts. `skewed` selects the heavy-tailed balance distribution;
    /// both distributions give the same account count and the same surplus of the assets over the liabilities.
    fn sz3_witness(state: &mut u64, skewed: bool) -> Witness {
        const SZ3_ACCOUNTS: usize = 100;
        const SZ3_SURPLUS: u64 = 1_000_000;
        let mut leaves = Vec::with_capacity(SZ3_ACCOUNTS);
        let mut liabilities = 0;
        for _ in 0..SZ3_ACCOUNTS {
            let balance = if skewed {
                // The bit length is uniform, so most accounts are small and a few are close to 2^40.
                let bits = 1 + splitmix64(state) % 39;
                splitmix64(state) & ((1u64 << bits) - 1)
            } else {
                splitmix64(state) % (1 << 24)
            };
            liabilities += balance;
            leaves.push(LeafData { id: sz3_field4(state), salt: sz3_field4(state), balance });
        }
        Witness { leaves, assets: liabilities + SZ3_SURPLUS, blinding: sz3_field4(state) }
    }

    /// SZ3 sample generator, ignored by default. Writes `SZ3_N` proofs, each of a fresh witness drawn from the
    /// distribution `SZ3_SET` (`uniform` or `skewed`), into `SZ3_OUT`. Run with:
    /// `SZ3_OUT=<dir> SZ3_SET=uniform SZ3_N=200 cargo test --release sz3_write_proof_samples -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn sz3_write_proof_samples() {
        let dir = std::env::var("SZ3_OUT").expect("SZ3_OUT must name the output directory");
        let set = std::env::var("SZ3_SET").expect("SZ3_SET must be `uniform` or `skewed`");
        let count: usize = std::env::var("SZ3_N").expect("SZ3_N must be a number").parse().unwrap();
        let skewed = match set.as_str() {
            "uniform" => false,
            "skewed" => true,
            other => panic!("unknown SZ3_SET `{other}`"),
        };
        std::fs::create_dir_all(&dir).unwrap();

        let depth = crate::hash::depth_for(100);
        // The seed differs per set so the two sets do not share ids, salts or blinding factors.
        let mut state = if skewed { 0x5A33_5A33_5A33_5A34 } else { 0x5A33_5A33_5A33_5A33 };
        for index in 0..count {
            let witness = sz3_witness(&mut state, skewed);
            let (bytes, _) = prove_solvency(depth, &witness, F::from_u64(7)).unwrap();
            std::fs::write(format!("{dir}/{set}_{index:04}.bin"), &bytes).unwrap();
        }
    }
}
