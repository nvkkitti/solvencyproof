//! The AIR that proves solvency without revealing any balance.
//!
//! # Statement
//!
//! Public values: the root digest of the liabilities sum tree, the commitment `C_A` to the
//! assets, and a nonce binding the proof to one audit. Private witness: every leaf
//! (account id, salt, balance), the assets `a` and its blinding factor `r`.
//!
//! The proof convinces a verifier that
//!
//! 1. every one of the `2^depth` leaf slots has a valid Poseidon2 path to the public root,
//!    and each node hash also commits to the sum of the balances below it, so the root sum `T`
//!    is the real sum of all balances;
//! 2. every balance is in `[0, 2^BALANCE_BITS)`, so no sum can wrap around the field;
//! 3. `C_A = Poseidon2(a, r)`;
//! 4. `a - T` is in `[0, 2^DIFF_BITS)`, that is `a >= T` (this is the actual solvency check).
//!
//! # Trace layout
//!
//! The trace is `2^depth` blocks of `B` rows (`B` a power of two, `B >= depth + 2`), one block
//! per leaf. Every row embeds one full Poseidon2 permutation; the phase `k` of the row inside
//! its block decides what that permutation hashes:
//!
//! | phase `k`          | row type | hashes                                          |
//! |--------------------|----------|-------------------------------------------------|
//! | `0`                | leaf     | the leaf `(id, balance, salt)`                  |
//! | `1 ..= depth`      | node     | the current node and its sibling, ordered by `dir` |
//! | `depth + 1`        | commit   | the assets commitment `(a, r)`                  |
//! | `depth + 2 .. B`   | padding  | nothing (all-zero input)                        |
//!
//! The phase flags are periodic columns, so a prover cannot choose them.

use core::borrow::Borrow;
use std::borrow::Cow;

use p3_air::{Air, AirBuilder, BaseAir, WindowAccess};
use p3_field::PrimeCharacteristicRing;
use p3_goldilocks::{
    GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_FINAL, GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_INITIAL,
    GOLDILOCKS_POSEIDON2_RC_12_INTERNAL, GenericPoseidon2LinearLayersGoldilocks,
};
use p3_poseidon2::{ExternalLayerConstants, GenericPoseidon2LinearLayers};
use p3_poseidon2_air::{FullRound, PartialRound, Poseidon2Cols, RoundConstants, SBox, num_cols};

use crate::hash::{
    BALANCE_BITS, DIFF_BITS, F, HALF_FULL_ROUNDS, PARTIAL_ROUNDS, SBOX_DEGREE, SBOX_REGISTERS,
    TAG_COMMIT, TAG_LEAF, TAG_NODE, WIDTH,
};

pub type PermCols<T> =
    Poseidon2Cols<T, WIDTH, SBOX_DEGREE, SBOX_REGISTERS, HALF_FULL_ROUNDS, PARTIAL_ROUNDS>;
pub type Constants = RoundConstants<F, WIDTH, HALF_FULL_ROUNDS, PARTIAL_ROUNDS>;

// ---- own columns (in front of the Poseidon2 columns) ----
/// Index of the leaf this block belongs to.
pub const C_IDX: usize = 0;
/// The node whose parent the row hashes (4 digest elements) and the sum below it.
pub const C_CUR: usize = 1;
pub const C_CUR_SUM: usize = 5;
/// The sibling node and its sum.
pub const C_SIB: usize = 6;
pub const C_SIB_SUM: usize = 10;
/// 1 when the current node is the right child.
pub const C_DIR: usize = 11;
/// Leaf index bits collected so far along the path.
pub const C_ACC: usize = 12;
/// Assets, assets blinding factor (4 elements) and total liabilities; constant over the trace.
pub const C_A: usize = 13;
pub const C_R: usize = 14;
pub const C_TOT: usize = 18;
/// Leaf data used by the leaf row.
pub const C_LID: usize = 19;
pub const C_SALT: usize = 23;
pub const C_BAL: usize = 27;
/// Bit decomposition: of the balance on leaf rows, of `a - T` on the last row.
pub const C_BITS: usize = 28;
pub const OWN_WIDTH: usize = C_BITS + DIFF_BITS;

pub const PERM_WIDTH: usize =
    num_cols::<WIDTH, SBOX_DEGREE, SBOX_REGISTERS, HALF_FULL_ROUNDS, PARTIAL_ROUNDS>();
pub const TRACE_WIDTH: usize = OWN_WIDTH + PERM_WIDTH;

// ---- public values ----
pub const PV_ROOT: usize = 0;
pub const PV_COMMITMENT: usize = 4;
pub const PV_NONCE: usize = 8;
pub const NUM_PUBLIC_VALUES: usize = 9;

// ---- periodic columns ----
const P_LEAF: usize = 0;
const P_NODE: usize = 1;
const P_LAST_NODE: usize = 2;
const P_NODE_CONT: usize = 3;
const P_COMMIT: usize = 4;
const P_POW: usize = 5;
const P_LAST_PHASE: usize = 6;
const NUM_PERIODIC: usize = 7;

/// Rows per block: leaf row, `depth` node rows, commit row, rounded up to a power of two.
pub const fn block_len(depth: usize) -> usize {
    (depth + 2).next_power_of_two()
}

#[derive(Clone)]
pub struct SolvencyAir {
    depth: usize,
    constants: Constants,
    periodic: Vec<Vec<F>>,
}

impl SolvencyAir {
    pub fn new(depth: usize) -> Self {
        let constants = Constants::try_from_layers(
            &ExternalLayerConstants::new(
                GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_INITIAL.to_vec(),
                GOLDILOCKS_POSEIDON2_RC_12_EXTERNAL_FINAL.to_vec(),
            ),
            &GOLDILOCKS_POSEIDON2_RC_12_INTERNAL,
        )
        .expect("the Goldilocks width-12 constants match the AIR shape");

        let b = block_len(depth);
        let flag = |pred: &dyn Fn(usize) -> bool| -> Vec<F> {
            (0..b).map(|k| if pred(k) { F::ONE } else { F::ZERO }).collect()
        };
        let mut periodic = vec![Vec::new(); NUM_PERIODIC];
        periodic[P_LEAF] = flag(&|k| k == 0);
        periodic[P_NODE] = flag(&|k| (1..=depth).contains(&k));
        periodic[P_LAST_NODE] = flag(&|k| k == depth);
        periodic[P_NODE_CONT] = flag(&|k| (1..depth).contains(&k));
        periodic[P_COMMIT] = flag(&|k| k == depth + 1);
        periodic[P_POW] = (0..b)
            .map(|k| if (1..=depth).contains(&k) { F::from_u64(1u64 << (k - 1)) } else { F::ZERO })
            .collect();
        periodic[P_LAST_PHASE] = flag(&|k| k == b - 1);

        Self { depth, constants, periodic }
    }

    pub fn depth(&self) -> usize {
        self.depth
    }

    pub fn constants(&self) -> &Constants {
        &self.constants
    }

    pub fn num_rows(&self) -> usize {
        (1usize << self.depth) * block_len(self.depth)
    }
}

impl BaseAir<F> for SolvencyAir {
    fn width(&self) -> usize {
        TRACE_WIDTH
    }

    fn num_public_values(&self) -> usize {
        NUM_PUBLIC_VALUES
    }

    fn num_periodic_columns(&self) -> usize {
        NUM_PERIODIC
    }

    fn periodic_columns(&self) -> Cow<'_, [Vec<F>]> {
        Cow::Borrowed(&self.periodic)
    }

    fn max_constraint_degree(&self) -> Option<usize> {
        Some(3)
    }
}

impl<AB: AirBuilder<F = F>> Air<AB> for SolvencyAir {
    fn eval(&self, builder: &mut AB) {
        let main = builder.main();
        let local = main.current_slice();
        let next = main.next_slice();

        let public: Vec<AB::Expr> = builder.public_values().iter().map(|&v| v.into()).collect();
        let periodic: Vec<AB::Expr> =
            builder.periodic_values().iter().map(|&v| v.into()).collect();

        let cur = |c: usize| -> AB::Expr { local[c].into() };
        let nxt = |c: usize| -> AB::Expr { next[c].into() };
        let constant = |v: u64| -> AB::Expr { AB::Expr::from_u64(v) };

        let is_leaf = periodic[P_LEAF].clone();
        let is_node = periodic[P_NODE].clone();
        let is_last_node = periodic[P_LAST_NODE].clone();
        let is_node_cont = periodic[P_NODE_CONT].clone();
        let is_commit = periodic[P_COMMIT].clone();
        let pow = periodic[P_POW].clone();
        let is_last_phase = periodic[P_LAST_PHASE].clone();

        let perm: &PermCols<AB::Var> = local[OWN_WIDTH..].borrow();

        // ---- booleans ----
        builder.assert_bool(local[C_DIR]);
        for i in 0..DIFF_BITS {
            builder.assert_bool(local[C_BITS + i]);
        }

        // ---- what the permutation of this row hashes, by row type ----
        let dir = cur(C_DIR);
        let left: [AB::Expr; 4] = core::array::from_fn(|k| {
            cur(C_CUR + k) + dir.clone() * (cur(C_SIB + k) - cur(C_CUR + k))
        });
        let right: [AB::Expr; 4] = core::array::from_fn(|k| {
            cur(C_SIB + k) + dir.clone() * (cur(C_CUR + k) - cur(C_SIB + k))
        });
        let left_sum = cur(C_CUR_SUM) + dir.clone() * (cur(C_SIB_SUM) - cur(C_CUR_SUM));
        let right_sum = cur(C_SIB_SUM) + dir.clone() * (cur(C_CUR_SUM) - cur(C_SIB_SUM));
        let zero = || AB::Expr::ZERO;

        let leaf_input: [AB::Expr; WIDTH] = [
            cur(C_LID),
            cur(C_LID + 1),
            cur(C_LID + 2),
            cur(C_LID + 3),
            cur(C_BAL),
            cur(C_SALT),
            cur(C_SALT + 1),
            cur(C_SALT + 2),
            cur(C_SALT + 3),
            zero(),
            zero(),
            constant(TAG_LEAF),
        ];
        let node_input: [AB::Expr; WIDTH] = [
            left[0].clone(),
            left[1].clone(),
            left[2].clone(),
            left[3].clone(),
            right[0].clone(),
            right[1].clone(),
            right[2].clone(),
            right[3].clone(),
            left_sum,
            right_sum,
            zero(),
            constant(TAG_NODE),
        ];
        let commit_input: [AB::Expr; WIDTH] = [
            cur(C_A),
            cur(C_R),
            cur(C_R + 1),
            cur(C_R + 2),
            cur(C_R + 3),
            zero(),
            zero(),
            zero(),
            zero(),
            zero(),
            zero(),
            constant(TAG_COMMIT),
        ];
        for j in 0..WIDTH {
            let expected = is_leaf.clone() * leaf_input[j].clone()
                + is_node.clone() * node_input[j].clone()
                + is_commit.clone() * commit_input[j].clone();
            builder.assert_eq(perm.inputs[j], expected);
        }

        // ---- the Poseidon2 permutation itself ----
        eval_permutation(&self.constants, builder, perm);
        let out: [AB::Expr; 4] =
            core::array::from_fn(|k| perm.ending_full_rounds[HALF_FULL_ROUNDS - 1].post[k].into());

        // ---- leaf rows: the balance is a 40-bit number ----
        let mut balance_bits = AB::Expr::ZERO;
        for i in 0..BALANCE_BITS {
            balance_bits += local[C_BITS + i] * F::from_u64(1u64 << i);
        }
        builder.assert_zero(is_leaf.clone() * (cur(C_BAL) - balance_bits));
        for i in BALANCE_BITS..DIFF_BITS {
            builder.assert_zero(is_leaf.clone() * cur(C_BITS + i));
        }

        // ---- the path: every row hands its result to the next row ----
        // The selectors vanish on the last row of the trace, so the wrap-around "next" row is harmless.
        for k in 0..4 {
            builder.assert_zero(is_leaf.clone() * (nxt(C_CUR + k) - out[k].clone()));
            builder.assert_zero(is_node_cont.clone() * (nxt(C_CUR + k) - out[k].clone()));
        }
        builder.assert_zero(is_leaf.clone() * (nxt(C_CUR_SUM) - cur(C_BAL)));
        builder.assert_zero(
            is_node_cont.clone() * (nxt(C_CUR_SUM) - (cur(C_CUR_SUM) + cur(C_SIB_SUM))),
        );
        builder.assert_zero(is_leaf.clone() * nxt(C_ACC));
        builder.assert_zero(
            is_node_cont.clone() * (nxt(C_ACC) - (cur(C_ACC) + dir.clone() * pow.clone())),
        );

        // ---- the top of the path: the public root, the total liabilities, the leaf index ----
        for k in 0..4 {
            builder.assert_zero(is_last_node.clone() * (out[k].clone() - public[PV_ROOT + k].clone()));
        }
        builder.assert_zero(
            is_last_node.clone() * (cur(C_CUR_SUM) + cur(C_SIB_SUM) - cur(C_TOT)),
        );
        builder.assert_zero(
            is_last_node.clone() * (cur(C_ACC) + dir.clone() * pow.clone() - cur(C_IDX)),
        );

        // ---- the assets commitment ----
        for k in 0..4 {
            builder.assert_zero(
                is_commit.clone() * (out[k].clone() - public[PV_COMMITMENT + k].clone()),
            );
        }

        // ---- values that must not change between rows, and the block counter ----
        {
            let mut transition = builder.when_transition();
            transition.assert_eq(nxt(C_A), cur(C_A));
            for k in 0..4 {
                transition.assert_eq(nxt(C_R + k), cur(C_R + k));
            }
            transition.assert_eq(nxt(C_TOT), cur(C_TOT));
            transition.assert_zero(nxt(C_IDX) - cur(C_IDX) - is_last_phase);
        }
        builder.when_first_row().assert_zero(cur(C_IDX));
        builder
            .when_last_row()
            .assert_zero(cur(C_IDX) - constant((1u64 << self.depth) - 1));

        // ---- the solvency check: assets - liabilities is a 62-bit number, hence non-negative ----
        let mut diff_bits = AB::Expr::ZERO;
        for i in 0..DIFF_BITS {
            diff_bits += local[C_BITS + i] * F::from_u64(1u64 << i);
        }
        builder
            .when_last_row()
            .assert_zero(cur(C_A) - cur(C_TOT) - diff_bits);
    }
}

/// Constraints of one Poseidon2 permutation, over the columns embedded in the row.
///
/// Mirrors `p3_poseidon2_air::eval`, which is crate-private, for the (degree 7, 1 register) S-box.
fn eval_permutation<AB: AirBuilder<F = F>>(
    constants: &Constants,
    builder: &mut AB,
    local: &PermCols<AB::Var>,
) {
    let mut state: [AB::Expr; WIDTH] = local.inputs.map(Into::into);

    GenericPoseidon2LinearLayersGoldilocks::external_linear_layer(&mut state);

    for round in 0..HALF_FULL_ROUNDS {
        eval_full_round(
            &mut state,
            &local.beginning_full_rounds[round],
            &constants.beginning_full_round_constants()[round],
            builder,
        );
    }
    for round in 0..PARTIAL_ROUNDS {
        eval_partial_round(
            &mut state,
            &local.partial_rounds[round],
            constants.partial_round_constants()[round],
            builder,
        );
    }
    for round in 0..HALF_FULL_ROUNDS {
        eval_full_round(
            &mut state,
            &local.ending_full_rounds[round],
            &constants.ending_full_round_constants()[round],
            builder,
        );
    }
}

fn eval_full_round<AB: AirBuilder<F = F>>(
    state: &mut [AB::Expr; WIDTH],
    full_round: &FullRound<AB::Var, WIDTH, SBOX_DEGREE, SBOX_REGISTERS>,
    round_constants: &[F; WIDTH],
    builder: &mut AB,
) {
    for (i, (s, r)) in state.iter_mut().zip(round_constants.iter()).enumerate() {
        *s += *r;
        eval_sbox(&full_round.sbox[i], s, builder);
    }
    GenericPoseidon2LinearLayersGoldilocks::external_linear_layer(state);
    for (state_i, post_i) in state.iter_mut().zip(full_round.post) {
        builder.assert_eq(state_i.clone(), post_i);
        *state_i = post_i.into();
    }
}

fn eval_partial_round<AB: AirBuilder<F = F>>(
    state: &mut [AB::Expr; WIDTH],
    partial_round: &PartialRound<AB::Var, SBOX_DEGREE, SBOX_REGISTERS>,
    round_constant: F,
    builder: &mut AB,
) {
    state[0] += round_constant;
    eval_sbox(&partial_round.sbox, &mut state[0], builder);

    builder.assert_eq(state[0].clone(), partial_round.post_sbox);
    state[0] = partial_round.post_sbox.into();

    GenericPoseidon2LinearLayersGoldilocks::internal_linear_layer(state);
}

/// `x^7` with one committed intermediate `x^3`, so every constraint has degree 3.
fn eval_sbox<AB: AirBuilder<F = F>>(
    sbox: &SBox<AB::Var, SBOX_DEGREE, SBOX_REGISTERS>,
    x: &mut AB::Expr,
    builder: &mut AB,
) {
    let committed_x3: AB::Expr = sbox.0[0].into();
    builder.assert_eq(committed_x3.clone(), x.cube());
    *x = committed_x3.square() * x.clone();
}
