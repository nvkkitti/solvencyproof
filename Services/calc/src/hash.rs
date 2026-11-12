//! Native Poseidon2 hashing over Goldilocks and the Merkle *sum* tree built on top of it.
//!
//! The same permutation is proven inside the AIR (see `air.rs`), so every hash computed here
//! is also something the STARK can check. The C# side ports this module and must produce
//! byte-identical digests (see `SolvencyProof.Helpers.Poseidon2Goldilocks`).

use std::sync::OnceLock;

use p3_field::{PrimeCharacteristicRing, PrimeField64};
use p3_goldilocks::{Goldilocks, Poseidon2Goldilocks, default_goldilocks_poseidon2_12};
use p3_symmetric::Permutation;

pub type F = Goldilocks;
/// A 256-bit digest: four Goldilocks elements.
pub type Digest = [F; 4];

pub const WIDTH: usize = 12;
pub const SBOX_DEGREE: u64 = 7;
pub const SBOX_REGISTERS: usize = 1;
pub const HALF_FULL_ROUNDS: usize = 4;
pub const PARTIAL_ROUNDS: usize = 22;

/// Every leaf balance must fit into this many bits (the AIR range-checks it).
pub const BALANCE_BITS: usize = 40;
/// `assets - liabilities` must fit into this many bits (the AIR range-checks it).
/// With at most 2^MAX_DEPTH leaves of < 2^BALANCE_BITS each, all sums stay far below the field
/// modulus, so no value can wrap around.
pub const DIFF_BITS: usize = 62;
/// The tree always has at least 2^MIN_DEPTH leaf slots; the hiding PCS needs a minimum trace height.
pub const MIN_DEPTH: usize = 6;
pub const MAX_DEPTH: usize = 20;

// Domain separation tags, always placed in the last element of the permutation input.
pub const TAG_LEAF: u64 = 1;
pub const TAG_NODE: u64 = 2;
pub const TAG_COMMIT: u64 = 3;

fn permutation() -> &'static Poseidon2Goldilocks<WIDTH> {
    static PERM: OnceLock<Poseidon2Goldilocks<WIDTH>> = OnceLock::new();
    PERM.get_or_init(default_goldilocks_poseidon2_12)
}

/// Truncated-permutation compression: permute the 12 inputs, keep the first 4 elements.
pub fn compress(mut state: [F; WIDTH]) -> Digest {
    permutation().permute_mut(&mut state);
    [state[0], state[1], state[2], state[3]]
}

/// The private data of one account leaf.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LeafData {
    /// Account id, 128 bits as four 32-bit limbs.
    pub id: [F; 4],
    /// Per-leaf random salt, so a low-entropy balance cannot be brute-forced from the leaf hash.
    pub salt: [F; 4],
    /// Balance in the smallest currency unit, `< 2^BALANCE_BITS`.
    pub balance: u64,
}

impl LeafData {
    /// The padding leaf that fills the unused slots of the tree.
    pub const EMPTY: LeafData = LeafData {
        id: [F::ZERO; 4],
        salt: [F::ZERO; 4],
        balance: 0,
    };
}

pub fn leaf_input(leaf: &LeafData) -> [F; WIDTH] {
    [
        leaf.id[0],
        leaf.id[1],
        leaf.id[2],
        leaf.id[3],
        F::from_u64(leaf.balance),
        leaf.salt[0],
        leaf.salt[1],
        leaf.salt[2],
        leaf.salt[3],
        F::ZERO,
        F::ZERO,
        F::from_u64(TAG_LEAF),
    ]
}

pub fn node_input(left: &Digest, right: &Digest, left_sum: F, right_sum: F) -> [F; WIDTH] {
    [
        left[0],
        left[1],
        left[2],
        left[3],
        right[0],
        right[1],
        right[2],
        right[3],
        left_sum,
        right_sum,
        F::ZERO,
        F::from_u64(TAG_NODE),
    ]
}

pub fn commit_input(assets: F, blinding: &[F; 4]) -> [F; WIDTH] {
    let mut input = [F::ZERO; WIDTH];
    input[0] = assets;
    input[1..5].copy_from_slice(blinding);
    input[WIDTH - 1] = F::from_u64(TAG_COMMIT);
    input
}

/// Hiding and binding commitment to the assets: `Poseidon2(assets, r)` with a 256-bit blinding `r`.
pub fn commit_assets(assets: u64, blinding: &[F; 4]) -> Digest {
    compress(commit_input(F::from_u64(assets), blinding))
}

/// A node of the sum tree: the hash together with the sum of all balances below it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Node {
    pub digest: Digest,
    pub sum: F,
}

pub fn leaf_node(leaf: &LeafData) -> Node {
    Node {
        digest: compress(leaf_input(leaf)),
        sum: F::from_u64(leaf.balance),
    }
}

pub fn parent_node(left: &Node, right: &Node) -> Node {
    Node {
        digest: compress(node_input(&left.digest, &right.digest, left.sum, right.sum)),
        sum: left.sum + right.sum,
    }
}

/// One step of an inclusion proof: the sibling node, and whether the proven node is the right child.
/// Inclusion proofs are served by the C# side; this mirrors that logic so the tests can cross-check it.
#[allow(dead_code)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PathStep {
    pub sibling: Node,
    pub is_right_child: bool,
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum TreeError {
    #[error("the tree depth must be between {MIN_DEPTH} and {MAX_DEPTH}, got {0}")]
    DepthOutOfRange(usize),
    #[error("{count} leaves do not fit into a tree of depth {depth}")]
    TooManyLeaves { count: usize, depth: usize },
    #[error("leaf {index} has a balance of {balance}, which does not fit into {BALANCE_BITS} bits")]
    BalanceOutOfRange { index: usize, balance: u64 },
    #[error("a field element is not in canonical form (>= the Goldilocks modulus)")]
    NonCanonicalElement,
}

/// The smallest depth that fits `leaf_count` leaves, but never below [`MIN_DEPTH`].
pub fn depth_for(leaf_count: usize) -> usize {
    let needed = leaf_count.max(1).next_power_of_two().trailing_zeros() as usize;
    needed.max(MIN_DEPTH)
}

/// The full sum tree; `levels[0]` are the leaves, the last level holds only the root.
pub struct SumTree {
    pub depth: usize,
    pub levels: Vec<Vec<Node>>,
}

impl SumTree {
    pub fn build(leaves: &[LeafData], depth: usize) -> Result<Self, TreeError> {
        for (index, leaf) in leaves.iter().enumerate() {
            if leaf.balance >> BALANCE_BITS != 0 {
                return Err(TreeError::BalanceOutOfRange { index, balance: leaf.balance });
            }
        }
        Self::build_without_range_check(leaves, depth)
    }

    /// Builds the tree without the balance range check. Production code must use [`Self::build`];
    /// this exists so the tests can forge the traces a dishonest prover would try.
    pub(crate) fn build_without_range_check(
        leaves: &[LeafData],
        depth: usize,
    ) -> Result<Self, TreeError> {
        if !(MIN_DEPTH..=MAX_DEPTH).contains(&depth) {
            return Err(TreeError::DepthOutOfRange(depth));
        }
        let slots = 1usize << depth;
        if leaves.len() > slots {
            return Err(TreeError::TooManyLeaves { count: leaves.len(), depth });
        }

        let empty = leaf_node(&LeafData::EMPTY);
        let mut level: Vec<Node> = (0..slots)
            .map(|i| leaves.get(i).map_or(empty, leaf_node))
            .collect();
        let mut levels = vec![level.clone()];
        while level.len() > 1 {
            level = level.chunks(2).map(|pair| parent_node(&pair[0], &pair[1])).collect();
            levels.push(level.clone());
        }

        Ok(Self { depth, levels })
    }

    pub fn root(&self) -> Node {
        self.levels[self.depth][0]
    }

    /// The sibling path from leaf `index` up to the root (`depth` steps).
    #[allow(dead_code)]
    pub fn path(&self, index: usize) -> Vec<PathStep> {
        (0..self.depth)
            .map(|level| {
                let position = index >> level;
                PathStep {
                    sibling: self.levels[level][position ^ 1],
                    is_right_child: position & 1 == 1,
                }
            })
            .collect()
    }
}

/// Recompute the root from a leaf node and its path.
#[allow(dead_code)]
pub fn root_from_path(leaf: Node, path: &[PathStep]) -> Node {
    path.iter().fold(leaf, |current, step| {
        if step.is_right_child {
            parent_node(&step.sibling, &current)
        } else {
            parent_node(&current, &step.sibling)
        }
    })
}

// ---- encodings shared with the C# side ----

/// Digests travel as 32 bytes: four canonical `u64` values, big-endian, concatenated.
pub fn digest_to_bytes(digest: &Digest) -> [u8; 32] {
    let mut bytes = [0u8; 32];
    for (chunk, element) in bytes.chunks_mut(8).zip(digest) {
        chunk.copy_from_slice(&element.as_canonical_u64().to_be_bytes());
    }
    bytes
}

pub fn digest_from_bytes(bytes: &[u8; 32]) -> Result<Digest, TreeError> {
    let mut digest = [F::ZERO; 4];
    for (element, chunk) in digest.iter_mut().zip(bytes.chunks(8)) {
        let value = u64::from_be_bytes(chunk.try_into().expect("chunks of 8"));
        *element = field_from_canonical(value)?;
    }
    Ok(digest)
}

/// Accept only canonical representatives, so one value has exactly one encoding.
pub fn field_from_canonical(value: u64) -> Result<F, TreeError> {
    if value >= F::ORDER_U64 {
        return Err(TreeError::NonCanonicalElement);
    }
    Ok(F::from_u64(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_leaf(i: u64) -> LeafData {
        LeafData {
            id: [F::from_u64(i), F::from_u64(i + 1), F::from_u64(i + 2), F::from_u64(i + 3)],
            salt: [F::from_u64(7 * i + 1), F::from_u64(11), F::from_u64(13), F::from_u64(17)],
            balance: 1_000 * (i + 1),
        }
    }

    #[test]
    fn root_sum_is_the_sum_of_all_balances() {
        let leaves: Vec<_> = (0..5).map(sample_leaf).collect();
        let tree = SumTree::build(&leaves, MIN_DEPTH).unwrap();
        let expected: u64 = leaves.iter().map(|l| l.balance).sum();
        assert_eq!(tree.root().sum, F::from_u64(expected));
    }

    #[test]
    fn every_path_leads_to_the_root() {
        let leaves: Vec<_> = (0..9).map(sample_leaf).collect();
        let tree = SumTree::build(&leaves, MIN_DEPTH).unwrap();
        for index in [0usize, 1, 2, 8, 9, 63] {
            let leaf = tree.levels[0][index];
            assert_eq!(root_from_path(leaf, &tree.path(index)), tree.root(), "index {index}");
        }
    }

    #[test]
    fn a_changed_balance_changes_the_root() {
        let mut leaves: Vec<_> = (0..4).map(sample_leaf).collect();
        let before = SumTree::build(&leaves, MIN_DEPTH).unwrap().root();
        leaves[2].balance += 1;
        let after = SumTree::build(&leaves, MIN_DEPTH).unwrap().root();
        assert_ne!(before.digest, after.digest);
    }

    #[test]
    fn rejects_balances_that_overflow_the_range() {
        let mut leaves = vec![sample_leaf(0)];
        leaves[0].balance = 1 << BALANCE_BITS;
        assert!(matches!(
            SumTree::build(&leaves, MIN_DEPTH),
            Err(TreeError::BalanceOutOfRange { index: 0, .. })
        ));
    }

    #[test]
    fn digest_bytes_round_trip_and_reject_non_canonical_values() {
        let digest = compress(leaf_input(&sample_leaf(3)));
        assert_eq!(digest_from_bytes(&digest_to_bytes(&digest)).unwrap(), digest);

        let mut bad = digest_to_bytes(&digest);
        bad[..8].copy_from_slice(&u64::MAX.to_be_bytes());
        assert_eq!(digest_from_bytes(&bad), Err(TreeError::NonCanonicalElement));
    }

    #[test]
    fn commitment_depends_on_the_blinding_factor() {
        let a = commit_assets(5_000, &[F::from_u64(1); 4]);
        let b = commit_assets(5_000, &[F::from_u64(2); 4]);
        assert_ne!(a, b);
    }

    /// Fixed vectors the C# port is checked against (`Poseidon2GoldilocksTests`).
    /// If this test fails after a dependency update, the C# vectors must be regenerated.
    #[test]
    fn print_cross_language_vectors() {
        let leaf = sample_leaf(3);
        let leaf_digest = compress(leaf_input(&leaf));
        let zero_digest = compress([F::ZERO; WIDTH]);
        eprintln!("zero-perm digest: {:?}", digest_to_bytes(&zero_digest));
        eprintln!("leaf digest hex: {}", hex::encode(digest_to_bytes(&leaf_digest)));
    }

    /// Writes the C# source with the Poseidon2 constants, so the port never relies on hand-copied numbers.
    /// Run with: `CSHARP_CONSTANTS_OUT=<path> cargo test --release write_csharp_constants -- --ignored`
    #[test]
    #[ignore]
    fn write_csharp_constants() {
        use p3_goldilocks::{
            GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_FINAL, GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_INITIAL,
            GOLDILOCKS_POSEIDON2_RC_12_INTERNAL, MATRIX_DIAG_12_GOLDILOCKS,
        };

        let row = |values: &[F]| {
            let items: Vec<String> = values
                .iter()
                .map(|v| format!("0x{:016X}UL", v.as_canonical_u64()))
                .collect();
            format!("new ulong[] {{ {} }}", items.join(", "))
        };
        let matrix = |rows: &[[F; WIDTH]]| {
            rows.iter()
                .map(|r| format!("        {},", row(r)))
                .collect::<Vec<_>>()
                .join("\n")
        };

        let source = format!(
            "// <auto-generated>\n\
             // Generated from the Plonky3 Goldilocks Poseidon2 (width 12) constants by calc/src/hash.rs\n\
             // (test `write_csharp_constants`). Do not edit by hand.\n\
             // </auto-generated>\n\
             namespace SolvencyProof.Helpers\n{{\n\
             \x20   internal static class Poseidon2GoldilocksConstants\n    {{\n\
             \x20       public static readonly ulong[][] ExternalInitial =\n        [\n{}\n        ];\n\n\
             \x20       public static readonly ulong[][] ExternalFinal =\n        [\n{}\n        ];\n\n\
             \x20       public static readonly ulong[] Internal = {};\n\n\
             \x20       public static readonly ulong[] InternalDiagonal = {};\n    }}\n}}\n",
            matrix(&GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_INITIAL),
            matrix(&GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_FINAL),
            row(&GOLDILOCKS_POSEIDON2_RC_12_INTERNAL),
            row(&MATRIX_DIAG_12_GOLDILOCKS),
        );

        let path = std::env::var("CSHARP_CONSTANTS_OUT").expect("set CSHARP_CONSTANTS_OUT");
        std::fs::write(path, source).unwrap();
    }
}
