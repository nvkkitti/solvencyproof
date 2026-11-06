// SPDX-License-Identifier: MIT
pragma solidity ^0.8.19;

contract MockStarkVerifier {
    // Ez a függvény szimulálja a bonyolult STARK matematikát
    // minden olyan bizonyítékot elfogad, aminek van tartalma jelenleg
    function verifyProof(
        uint256[] calldata proof, 
        uint256[] calldata publicInputs
    ) external pure returns (bool) {
        if (proof.length > 0 && publicInputs.length >= 2) {
            return true;
        }
        return false;
    }
}