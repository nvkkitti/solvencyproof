//! Builds the execution trace of [`SolvencyAir`](crate::air::SolvencyAir) from the private witness.

use core::borrow::BorrowMut;
use core::mem::MaybeUninit;

use p3_field::{PrimeCharacteristicRing, PrimeField64};
use p3_goldilocks::GenericPoseidon2LinearLayersGoldilocks;
use p3_matrix::dense::RowMajorMatrix;
use p3_poseidon2_air::generate_trace_rows_for_perm;

use crate::air::{
    C_A, C_ACC, C_BAL, C_BITS, C_CUR, C_CUR_SUM, C_DIR, C_IDX, C_LID, C_R, C_SALT, C_SIB,
    C_SIB_SUM, C_TOT, NUM_PUBLIC_VALUES, OWN_WIDTH, PV_COMMITMENT, PV_NONCE, PV_ROOT, PermCols,
    SolvencyAir, TRACE_WIDTH, block_len,
};
use crate::hash::{
    BALANCE_BITS, DIFF_BITS, Digest, F, HALF_FULL_ROUNDS, LeafData, PARTIAL_ROUNDS, SBOX_DEGREE,
    SBOX_REGISTERS, SumTree, TreeError, WIDTH, commit_assets, commit_input, leaf_input,
    node_input,
};

/// Everything the prover knows and the verifier must never learn.
pub struct Witness {
    pub leaves: Vec<LeafData>,
    pub assets: u64,
    pub blinding: [F; 4],
}

/// What a verifier sees: the root of the liabilities tree, the commitment to the assets and the audit nonce.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PublicInputs {
    pub root: Digest,
    pub commitment: Digest,
    pub nonce: F,
}

impl PublicInputs {
    pub fn to_values(&self) -> Vec<F> {
        let mut values = vec![F::ZERO; NUM_PUBLIC_VALUES];
        values[PV_ROOT..PV_ROOT + 4].copy_from_slice(&self.root);
        values[PV_COMMITMENT..PV_COMMITMENT + 4].copy_from_slice(&self.commitment);
        values[PV_NONCE] = self.nonce;
        values
    }
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum TraceError {
    #[error(transparent)]
    Tree(#[from] TreeError),
    #[error("the assets ({assets}) are lower than the liabilities ({liabilities}), no solvency proof exists")]
    Insolvent { assets: u64, liabilities: u64 },
    #[error("the assets exceed the liabilities by more than 2^{DIFF_BITS}")]
    SurplusOutOfRange,
}

/// Computes the public inputs of a witness without building the (large) trace.
pub fn public_inputs(
    depth: usize,
    witness: &Witness,
    nonce: F,
) -> Result<(PublicInputs, SumTree), TraceError> {
    let tree = SumTree::build(&witness.leaves, depth)?;
    Ok((inputs_of(witness, &tree, nonce), tree))
}

fn inputs_of(witness: &Witness, tree: &SumTree, nonce: F) -> PublicInputs {
    PublicInputs {
        root: tree.root().digest,
        commitment: commit_assets(witness.assets, &witness.blinding),
        nonce,
    }
}

pub fn generate_trace(
    air: &SolvencyAir,
    witness: &Witness,
    nonce: F,
) -> Result<(RowMajorMatrix<F>, PublicInputs), TraceError> {
    let tree = SumTree::build(&witness.leaves, air.depth())?;
    build_trace(air, witness, nonce, &tree, true)
}

/// Builds the trace of a witness an honest prover would refuse to prove (insolvent, balance out of
/// range), to check that the AIR itself rejects it.
#[cfg(test)]
pub fn generate_forged_trace(
    air: &SolvencyAir,
    witness: &Witness,
    nonce: F,
) -> Result<(RowMajorMatrix<F>, PublicInputs), TraceError> {
    let tree = SumTree::build_without_range_check(&witness.leaves, air.depth())?;
    build_trace(air, witness, nonce, &tree, false)
}

fn build_trace(
    air: &SolvencyAir,
    witness: &Witness,
    nonce: F,
    tree: &SumTree,
    enforce_solvency: bool,
) -> Result<(RowMajorMatrix<F>, PublicInputs), TraceError> {
    let depth = air.depth();
    let inputs = inputs_of(witness, tree, nonce);

    let liabilities = tree.root().sum.as_canonical_u64();
    if enforce_solvency && witness.assets < liabilities {
        return Err(TraceError::Insolvent { assets: witness.assets, liabilities });
    }
    // Wrapping, so a forged (insolvent) witness still produces a trace; its bits cannot add up.
    let surplus = witness.assets.wrapping_sub(liabilities);
    if enforce_solvency && surplus >> DIFF_BITS != 0 {
        return Err(TraceError::SurplusOutOfRange);
    }

    let block = block_len(depth);
    let rows = air.num_rows();
    let assets = F::from_u64(witness.assets);
    let total = tree.root().sum;
    let mut values = F::zero_vec(rows * TRACE_WIDTH);

    for (row_index, row) in values.chunks_mut(TRACE_WIDTH).enumerate() {
        let leaf_index = row_index / block;
        let phase = row_index % block;
        let (own, perm_part) = row.split_at_mut(OWN_WIDTH);

        own[C_IDX] = F::from_u64(leaf_index as u64);
        own[C_A] = assets;
        own[C_R..C_R + 4].copy_from_slice(&witness.blinding);
        own[C_TOT] = total;

        let perm_input = if phase == 0 {
            let leaf = witness.leaves.get(leaf_index).copied().unwrap_or(LeafData::EMPTY);
            own[C_LID..C_LID + 4].copy_from_slice(&leaf.id);
            own[C_SALT..C_SALT + 4].copy_from_slice(&leaf.salt);
            own[C_BAL] = F::from_u64(leaf.balance);
            for i in 0..BALANCE_BITS {
                own[C_BITS + i] = F::from_u64((leaf.balance >> i) & 1);
            }
            leaf_input(&leaf)
        } else if phase <= depth {
            let level = phase - 1;
            let position = leaf_index >> level;
            let current = tree.levels[level][position];
            let sibling = tree.levels[level][position ^ 1];
            let is_right_child = position & 1 == 1;

            own[C_CUR..C_CUR + 4].copy_from_slice(&current.digest);
            own[C_CUR_SUM] = current.sum;
            own[C_SIB..C_SIB + 4].copy_from_slice(&sibling.digest);
            own[C_SIB_SUM] = sibling.sum;
            own[C_DIR] = F::from_u64(is_right_child as u64);
            own[C_ACC] = F::from_u64((leaf_index & ((1usize << level) - 1)) as u64);

            if is_right_child {
                node_input(&sibling.digest, &current.digest, sibling.sum, current.sum)
            } else {
                node_input(&current.digest, &sibling.digest, current.sum, sibling.sum)
            }
        } else if phase == depth + 1 {
            commit_input(assets, &witness.blinding)
        } else {
            [F::ZERO; WIDTH]
        };

        // The last row of the trace carries the bits of `assets - liabilities`.
        if row_index == rows - 1 {
            for i in 0..DIFF_BITS {
                own[C_BITS + i] = F::from_u64((surplus >> i) & 1);
            }
        }

        fill_permutation(air, perm_part, perm_input);
    }

    Ok((RowMajorMatrix::new(values, TRACE_WIDTH), inputs))
}

fn fill_permutation(air: &SolvencyAir, columns: &mut [F], input: [F; WIDTH]) {
    // SAFETY: `MaybeUninit<F>` has the same layout as `F`, and every element is written below.
    let uninit: &mut [MaybeUninit<F>] =
        unsafe { &mut *(columns as *mut [F] as *mut [MaybeUninit<F>]) };
    let perm: &mut PermCols<MaybeUninit<F>> = uninit.borrow_mut();
    generate_trace_rows_for_perm::<
        F,
        GenericPoseidon2LinearLayersGoldilocks,
        WIDTH,
        SBOX_DEGREE,
        SBOX_REGISTERS,
        HALF_FULL_ROUNDS,
        PARTIAL_ROUNDS,
    >(perm, input, air.constants());
}
